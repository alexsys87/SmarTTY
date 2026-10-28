using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SmarTTY.Ssh;

/// <summary>
/// File attributes as reported by SFTP; a field is null when the server did not send it.
/// </summary>
public sealed class SftpFileAttributes
{
    public long? Size;

    /// <summary>
    /// The full st_mode value: file type bits and permission bits.
    /// </summary>
    public int? Mode;

    public DateTime? AccessTime;

    public DateTime? ModifyTime;

    public bool IsDirectory => (Mode & 0xF000) == 0x4000;

    public bool IsSymbolicLink => (Mode & 0xF000) == 0xA000;

    public bool IsRegularFile => (Mode & 0xF000) == 0x8000;
}

/// <summary>An entry of a directory listing (SSH_FXP_READDIR): the name and what the server says about it.</summary>
public sealed class SftpDirectoryEntry
{
    public string Name;

    /// <summary>The attributes of the entry itself: a symbolic link is reported as a link, not as what it points to.</summary>
    public SftpFileAttributes Attributes;
}

/// <summary>
/// An SFTP error reported by the server, with the SSH_FX_* status code.
/// </summary>
public sealed class SftpStatusException : RemoteOperationException
{
    public readonly int Status;

    public SftpStatusException(int status, string text)
        : base(text)
    {
        Status = status;
    }
}

/// <summary>
/// A minimal SFTP version 3 client (draft-ietf-secsh-filexfer-02) running on a "sftp" subsystem channel of an
/// existing <see cref="SshSession"/>, so no second login is needed. Reads and writes are pipelined: a bounded number
/// of requests is kept in flight, which also bounds the memory used, unlike an SCP stream that the server pushes
/// as fast as it can. An instance is meant for one thread at a time.
/// </summary>
public sealed class SftpChannel : IDisposable
{
    public const int StatusOk = 0;
    public const int StatusEof = 1;
    public const int StatusNoSuchFile = 2;
    public const int StatusPermissionDenied = 3;
    public const int StatusFailure = 4;

    [Flags]
    public enum OpenFlags : uint
    {
        Read = 0x01,
        Write = 0x02,
        Append = 0x04,
        Create = 0x08,
        Truncate = 0x10,
        Exclusive = 0x20
    }

    private const byte FxpInit = 1;
    private const byte FxpVersion = 2;
    private const byte FxpOpen = 3;
    private const byte FxpClose = 4;
    private const byte FxpRead = 5;
    private const byte FxpWrite = 6;
    private const byte FxpLstat = 7;
    private const byte FxpFstat = 8;
    private const byte FxpSetstat = 9;
    private const byte FxpOpendir = 11;
    private const byte FxpReaddir = 12;
    private const byte FxpRemove = 13;
    private const byte FxpMkdir = 14;
    private const byte FxpRmdir = 15;
    private const byte FxpRealpath = 16;
    private const byte FxpStat = 17;
    private const byte FxpRename = 18;
    private const byte FxpStatus = 101;
    private const byte FxpHandle = 102;
    private const byte FxpData = 103;
    private const byte FxpName = 104;
    private const byte FxpAttrs = 105;

    private const uint AttrSize = 0x01;
    private const uint AttrUidGid = 0x02;
    private const uint AttrPermissions = 0x04;
    private const uint AttrAccessModifyTime = 0x08;
    private const uint AttrExtended = 0x80000000;

    // OpenSSH serves up to 255 KB per read, other servers less; 32 KB is the size every server must accept.
    internal const int ChunkSize = 32768;

    private const int MaxRequestsInFlight = 32;

    // Largest packet accepted from the server: a data reply plus headers, with room for long file names.
    private const int MaxPacketSize = 1024 * 1024;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly SshShellChannel _channel;

    private readonly Dictionary<uint, byte[]> _earlyReplies = new Dictionary<uint, byte[]>();

    private uint _nextRequestId = 1;

    private bool _disposed;

    public int ServerVersion { get; private set; }

    private SftpChannel(SshShellChannel channel)
    {
        _channel = channel;
        _channel.Output.ReadFully = true;
    }

    /// <summary>
    /// Starts the SFTP subsystem. Throws <see cref="RemoteOperationException"/> if the server has none (for example
    /// Dropbear on OpenWrt without the openssh-sftp-server package).
    /// </summary>
    public static SftpChannel Open(SshSession session, string subsystem = "sftp")
    {
        SshShellChannel channel;
        try
        {
            channel = session.OpenSubsystemChannel(subsystem);
        }
        catch (Exception exception) when (!(exception is RemoteOperationException))
        {
            throw new RemoteOperationException("The server does not provide SFTP: " + exception.Message);
        }
        SftpChannel sftp = new SftpChannel(channel);
        try
        {
            sftp.Initialize();
            return sftp;
        }
        catch
        {
            sftp.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        PacketWriter init = new PacketWriter(FxpInit);
        init.WriteUInt32(3);
        Send(init);
        byte[] reply = ReceivePacket();
        if (reply == null)
        {
            // Dropbear accepts the subsystem request and closes the channel when sftp-server is not installed.
            throw new RemoteOperationException("The server does not provide SFTP (the sftp subsystem closed right away; on OpenWrt install openssh-sftp-server).");
        }
        PacketReader reader = new PacketReader(reply);
        if (reader.ReadByte() != FxpVersion)
        {
            throw new RemoteOperationException("The SFTP server did not answer the version handshake.");
        }
        ServerVersion = (int)reader.ReadUInt32();
        if (ServerVersion < 3)
        {
            throw new RemoteOperationException("The SFTP server speaks version " + ServerVersion + "; version 3 is required.");
        }
    }

    #region File operations

    /// <summary>
    /// Returns the attributes of <paramref name="path"/> (following symbolic links), or null if it does not exist.
    /// </summary>
    public SftpFileAttributes Stat(string path)
    {
        return StatCommon(FxpStat, path);
    }

    private SftpFileAttributes StatCommon(byte type, string path)
    {
        PacketWriter request = new PacketWriter(type);
        request.WriteString(path);
        PacketReader reply = Request(request, path, out byte replyType, allowNoSuchFile: true);
        if (reply == null)
        {
            return null;
        }
        ExpectType(replyType, FxpAttrs, path);
        return ReadAttributes(reply);
    }

    /// <summary>
    /// Like <see cref="Stat"/>, but a symbolic link is described itself, not what it points to; null if it does not exist.
    /// </summary>
    public SftpFileAttributes Lstat(string path)
    {
        return StatCommon(FxpLstat, path);
    }

    /// <summary>
    /// The entries of a directory (OPENDIR, then READDIR until the server answers end of file), in the order the server
    /// gives them and including "." and ".." where it sends them. Throws if the directory cannot be opened.
    /// </summary>
    public List<SftpDirectoryEntry> ListDirectory(string path)
    {
        PacketWriter open = new PacketWriter(FxpOpendir);
        open.WriteString(path);
        PacketReader openReply = Request(open, path, out byte openType);
        ExpectType(openType, FxpHandle, path);
        byte[] handle = openReply.ReadBytes();
        List<SftpDirectoryEntry> entries = new List<SftpDirectoryEntry>();
        try
        {
            while (true)
            {
                PacketWriter read = new PacketWriter(FxpReaddir);
                read.WriteBytes(handle);
                uint id = Send(read);
                PacketReader reply = ReceiveReply(id, out byte replyType);
                if (replyType == FxpStatus)
                {
                    int status = (int)reply.ReadUInt32();
                    if (status == StatusEof)
                    {
                        return entries;
                    }
                    throw StatusError(status, reply, path);
                }
                ExpectType(replyType, FxpName, path);
                uint count = reply.ReadUInt32();
                for (uint index = 0; index < count; index++)
                {
                    string name = reply.ReadString();
                    // The "long name" is the line of ls -l the server makes up; its layout is not defined, so it is not used.
                    reply.ReadString();
                    entries.Add(new SftpDirectoryEntry { Name = name, Attributes = ReadAttributes(reply) });
                }
            }
        }
        finally
        {
            try
            {
                CloseHandle(handle);
            }
            catch
            {
                // The listing is what the caller asked for; a handle that cannot be closed goes with the channel.
            }
        }
    }

    /// <summary>Creates a directory; <paramref name="mode"/> is the permission bits to ask for (null: the server decides).</summary>
    public void MakeDirectory(string path, int? mode = null)
    {
        PacketWriter request = new PacketWriter(FxpMkdir);
        request.WriteString(path);
        WriteAttributes(request, mode == null ? null : new SftpFileAttributes { Mode = mode });
        ExpectOk(request, path);
    }

    /// <summary>Removes an empty directory.</summary>
    public void RemoveDirectory(string path)
    {
        PacketWriter request = new PacketWriter(FxpRmdir);
        request.WriteString(path);
        ExpectOk(request, path);
    }

    public byte[] OpenFile(string path, OpenFlags flags, SftpFileAttributes attributes = null)
    {
        PacketWriter request = new PacketWriter(FxpOpen);
        request.WriteString(path);
        request.WriteUInt32((uint)flags);
        WriteAttributes(request, attributes);
        PacketReader reply = Request(request, path, out byte replyType);
        ExpectType(replyType, FxpHandle, path);
        return reply.ReadBytes();
    }

    public void CloseHandle(byte[] handle)
    {
        PacketWriter request = new PacketWriter(FxpClose);
        request.WriteBytes(handle);
        ExpectOk(request, null);
    }

    public void Remove(string path)
    {
        PacketWriter request = new PacketWriter(FxpRemove);
        request.WriteString(path);
        ExpectOk(request, path);
    }

    /// <summary>
    /// Renames a file; SFTP version 3 servers refuse to replace an existing target.
    /// </summary>
    public void Rename(string oldPath, string newPath)
    {
        PacketWriter request = new PacketWriter(FxpRename);
        request.WriteString(oldPath);
        request.WriteString(newPath);
        ExpectOk(request, oldPath);
    }

    public string RealPath(string path)
    {
        PacketWriter request = new PacketWriter(FxpRealpath);
        request.WriteString(path);
        PacketReader reply = Request(request, path, out byte replyType);
        ExpectType(replyType, FxpName, path);
        if (reply.ReadUInt32() < 1)
        {
            throw new RemoteOperationException("The SFTP server returned no name for " + path);
        }
        return reply.ReadString();
    }

    /// <summary>
    /// Opens <paramref name="path"/> for reading from <paramref name="offset"/>; the stream ends at the end of the file.
    /// </summary>
    public Stream OpenRead(string path, long offset)
    {
        byte[] handle = OpenFile(path, OpenFlags.Read);
        return new ReadStream(this, handle, offset);
    }

    /// <summary>
    /// Opens <paramref name="path"/> for writing at <paramref name="offset"/>, creating it if needed. With
    /// <paramref name="truncate"/> the previous content is dropped first. New files get <paramref name="mode"/>
    /// (minus the server's umask); disposing the stream waits for every write to be confirmed.
    /// </summary>
    public Stream OpenWrite(string path, long offset, bool truncate, int mode = 0x1A4)
    {
        OpenFlags flags = OpenFlags.Write | OpenFlags.Create | (truncate ? OpenFlags.Truncate : 0);
        byte[] handle = OpenFile(path, flags, new SftpFileAttributes { Mode = mode & 0xFFF });
        return new WriteStream(this, handle, offset);
    }

    #endregion

    #region Request plumbing

    private uint Send(PacketWriter packet)
    {
        uint id = _nextRequestId++;
        byte[] bytes = packet.ToArray(id);
        _channel.Input.Write(bytes, 0, bytes.Length);
        return id;
    }

    private PacketReader Request(PacketWriter packet, string path, out byte replyType, bool allowNoSuchFile = false)
    {
        uint id = Send(packet);
        PacketReader reply = ReceiveReply(id, out replyType);
        if (replyType == FxpStatus)
        {
            int status = (int)reply.ReadUInt32();
            if (status == StatusNoSuchFile && allowNoSuchFile)
            {
                return null;
            }
            throw StatusError(status, reply, path);
        }
        return reply;
    }

    private void ExpectOk(PacketWriter packet, string path)
    {
        uint id = Send(packet);
        CheckStatusReply(id, path);
    }

    private void CheckStatusReply(uint id, string path)
    {
        PacketReader reply = ReceiveReply(id, out byte replyType);
        ExpectType(replyType, FxpStatus, path);
        int status = (int)reply.ReadUInt32();
        if (status != StatusOk)
        {
            throw StatusError(status, reply, path);
        }
    }

    private static void ExpectType(byte replyType, byte expected, string path)
    {
        if (replyType != expected)
        {
            throw new RemoteOperationException("Unexpected SFTP reply " + replyType + ((path != null) ? (" for " + path) : ""));
        }
    }

    private static Exception StatusError(int status, PacketReader reply, string path)
    {
        string message = null;
        try
        {
            message = reply.ReadString();
        }
        catch
        {
        }
        if (string.IsNullOrEmpty(message))
        {
            message = status switch
            {
                StatusEof => "End of file",
                StatusNoSuchFile => "No such file",
                StatusPermissionDenied => "Permission denied",
                _ => "SFTP error " + status
            };
        }
        return new SftpStatusException(status, (path != null) ? (path + ": " + message) : message);
    }

    /// <summary>
    /// Waits for the reply to request <paramref name="id"/>; replies to other requests that arrive first are kept.
    /// </summary>
    private PacketReader ReceiveReply(uint id, out byte replyType)
    {
        if (!_earlyReplies.TryGetValue(id, out byte[] packet))
        {
            while (true)
            {
                packet = ReceivePacket() ?? throw new IOException("The SFTP channel was closed.");
                uint replyId = (packet.Length >= 5) ? ReadUInt32(packet, 1) : 0;
                if (replyId == id)
                {
                    break;
                }
                _earlyReplies[replyId] = packet;
            }
        }
        else
        {
            _earlyReplies.Remove(id);
        }
        PacketReader reader = new PacketReader(packet);
        replyType = reader.ReadByte();
        reader.ReadUInt32();
        return reader;
    }

    private byte[] ReceivePacket()
    {
        byte[] header = new byte[4];
        int read = _channel.Output.Read(header, 0, 4);
        if (read == 0)
        {
            return null;
        }
        if (read != 4)
        {
            throw new IOException("The SFTP channel was closed in the middle of a packet.");
        }
        uint length = ReadUInt32(header, 0);
        if (length == 0 || length > MaxPacketSize)
        {
            throw new IOException("Invalid SFTP packet length " + length + ".");
        }
        byte[] packet = new byte[length];
        if (_channel.Output.Read(packet, 0, packet.Length) != packet.Length)
        {
            throw new IOException("The SFTP channel was closed in the middle of a packet.");
        }
        return packet;
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
    {
        return (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer[offset + 2] << 8 | buffer[offset + 3]);
    }

    private static SftpFileAttributes ReadAttributes(PacketReader reader)
    {
        SftpFileAttributes attributes = new SftpFileAttributes();
        uint flags = reader.ReadUInt32();
        if ((flags & AttrSize) != 0)
        {
            attributes.Size = (long)reader.ReadUInt64();
        }
        if ((flags & AttrUidGid) != 0)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
        }
        if ((flags & AttrPermissions) != 0)
        {
            attributes.Mode = (int)reader.ReadUInt32();
        }
        if ((flags & AttrAccessModifyTime) != 0)
        {
            attributes.AccessTime = SshSession.UnixEpoch.AddSeconds(reader.ReadUInt32());
            attributes.ModifyTime = SshSession.UnixEpoch.AddSeconds(reader.ReadUInt32());
        }
        if ((flags & AttrExtended) != 0)
        {
            uint count = reader.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                reader.ReadBytes();
                reader.ReadBytes();
            }
        }
        return attributes;
    }

    private static void WriteAttributes(PacketWriter writer, SftpFileAttributes attributes)
    {
        uint flags = 0;
        if (attributes?.Size != null)
        {
            flags |= AttrSize;
        }
        if (attributes?.Mode != null)
        {
            flags |= AttrPermissions;
        }
        if (attributes?.ModifyTime != null)
        {
            flags |= AttrAccessModifyTime;
        }
        writer.WriteUInt32(flags);
        if (attributes?.Size != null)
        {
            writer.WriteUInt64((ulong)attributes.Size.Value);
        }
        if (attributes?.Mode != null)
        {
            writer.WriteUInt32((uint)attributes.Mode.Value);
        }
        if (attributes?.ModifyTime != null)
        {
            uint modifyTime = ToUnixTime(attributes.ModifyTime.Value);
            writer.WriteUInt32((attributes.AccessTime != null) ? ToUnixTime(attributes.AccessTime.Value) : modifyTime);
            writer.WriteUInt32(modifyTime);
        }
    }

    private static uint ToUnixTime(DateTime time)
    {
        double seconds = (time.ToUniversalTime() - SshSession.UnixEpoch).TotalSeconds;
        return (uint)Math.Max(0.0, Math.Min(uint.MaxValue, seconds));
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _channel.SendEof();
        }
        catch
        {
        }
        _channel.Dispose();
    }

    private sealed class PacketWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();

        public PacketWriter(byte type)
        {
            // Length and request id are filled in by ToArray.
            _stream.Write(new byte[4], 0, 4);
            _stream.WriteByte(type);
            if (type != FxpInit)
            {
                _stream.Write(new byte[4], 0, 4);
            }
        }

        public void WriteUInt32(uint value)
        {
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void WriteUInt64(ulong value)
        {
            WriteUInt32((uint)(value >> 32));
            WriteUInt32((uint)value);
        }

        public void WriteBytes(byte[] data, int offset, int count)
        {
            WriteUInt32((uint)count);
            _stream.Write(data, offset, count);
        }

        public void WriteBytes(byte[] data)
        {
            WriteBytes(data, 0, data.Length);
        }

        public void WriteString(string text)
        {
            WriteBytes(Utf8.GetBytes(text));
        }

        public byte[] ToArray(uint id)
        {
            byte[] bytes = _stream.ToArray();
            uint length = (uint)(bytes.Length - 4);
            bytes[0] = (byte)(length >> 24);
            bytes[1] = (byte)(length >> 16);
            bytes[2] = (byte)(length >> 8);
            bytes[3] = (byte)length;
            if (bytes[4] != FxpInit)
            {
                bytes[5] = (byte)(id >> 24);
                bytes[6] = (byte)(id >> 16);
                bytes[7] = (byte)(id >> 8);
                bytes[8] = (byte)id;
            }
            return bytes;
        }
    }

    private sealed class PacketReader
    {
        private readonly byte[] _buffer;

        private int _position;

        public PacketReader(byte[] buffer)
        {
            _buffer = buffer;
        }

        public int Remaining => _buffer.Length - _position;

        private void Require(int count)
        {
            if (count < 0 || _position + count > _buffer.Length)
            {
                throw new IOException("Truncated SFTP packet.");
            }
        }

        public byte ReadByte()
        {
            Require(1);
            return _buffer[_position++];
        }

        public uint ReadUInt32()
        {
            Require(4);
            uint value = SftpChannel.ReadUInt32(_buffer, _position);
            _position += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            return (ulong)ReadUInt32() << 32 | ReadUInt32();
        }

        public byte[] ReadBytes()
        {
            int count = (int)ReadUInt32();
            Require(count);
            byte[] data = new byte[count];
            System.Buffer.BlockCopy(_buffer, _position, data, 0, count);
            _position += count;
            return data;
        }

        /// <summary>
        /// Reads a length-prefixed block in place: returns its offset in <see cref="Buffer"/>.
        /// </summary>
        public int ReadBlock(out int count)
        {
            count = (int)ReadUInt32();
            Require(count);
            int offset = _position;
            _position += count;
            return offset;
        }

        public byte[] Buffer => _buffer;

        public string ReadString()
        {
            return Utf8.GetString(ReadBytes());
        }
    }

    /// <summary>
    /// Sequential reader with read-ahead: up to <see cref="MaxRequestsInFlight"/> chunks are requested in advance.
    /// </summary>
    private sealed class ReadStream : Stream
    {
        private struct Pending
        {
            public uint Id;
            public long Offset;
            public int Length;
        }

        private readonly SftpChannel _sftp;

        private readonly byte[] _handle;

        private readonly LinkedList<Pending> _pending = new LinkedList<Pending>();

        private long _nextRequestOffset;

        private long _position;

        private byte[] _current;

        private int _currentOffset;

        private int _currentCount;

        private bool _eof;

        private bool _closed;

        public ReadStream(SftpChannel sftp, byte[] handle, long offset)
        {
            _sftp = sftp;
            _handle = handle;
            _nextRequestOffset = offset;
            _position = offset;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        private void RequestMore()
        {
            while (!_eof && _pending.Count < MaxRequestsInFlight)
            {
                AddRequest(_nextRequestOffset, ChunkSize, atFront: false);
                _nextRequestOffset += ChunkSize;
            }
        }

        private void AddRequest(long offset, int length, bool atFront)
        {
            PacketWriter request = new PacketWriter(FxpRead);
            request.WriteBytes(_handle);
            request.WriteUInt64((ulong)offset);
            request.WriteUInt32((uint)length);
            Pending pending = new Pending { Id = _sftp.Send(request), Offset = offset, Length = length };
            if (atFront)
            {
                _pending.AddFirst(pending);
            }
            else
            {
                _pending.AddLast(pending);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_currentCount == 0)
            {
                if (_eof && _pending.Count == 0)
                {
                    return 0;
                }
                RequestMore();
                Pending head = _pending.First.Value;
                _pending.RemoveFirst();
                PacketReader reply = _sftp.ReceiveReply(head.Id, out byte replyType);
                if (replyType == FxpStatus)
                {
                    int status = (int)reply.ReadUInt32();
                    if (status != StatusEof)
                    {
                        throw StatusError(status, reply, null);
                    }
                    // Everything requested beyond the end is answered with EOF as well; drain those replies.
                    _eof = true;
                    continue;
                }
                ExpectType(replyType, FxpData, null);
                int dataOffset = reply.ReadBlock(out int dataCount);
                if (dataCount == 0)
                {
                    _eof = true;
                    continue;
                }
                if (dataCount < head.Length)
                {
                    // A short read that is not the end of the file: ask for the rest before anything later.
                    AddRequest(head.Offset + dataCount, head.Length - dataCount, atFront: true);
                }
                _current = reply.Buffer;
                _currentOffset = dataOffset;
                _currentCount = dataCount;
            }
            int done = Math.Min(count, _currentCount);
            System.Buffer.BlockCopy(_current, _currentOffset, buffer, offset, done);
            _currentOffset += done;
            _currentCount -= done;
            _position += done;
            return done;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                try
                {
                    // Outstanding replies must be consumed before the channel can be used for anything else.
                    while (_pending.Count != 0)
                    {
                        Pending head = _pending.First.Value;
                        _pending.RemoveFirst();
                        _sftp.ReceiveReply(head.Id, out _);
                    }
                    _sftp.CloseHandle(_handle);
                }
                catch
                {
                }
            }
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Sequential writer; up to <see cref="MaxRequestsInFlight"/> writes wait for their confirmation at a time.
    /// </summary>
    private sealed class WriteStream : Stream
    {
        private readonly SftpChannel _sftp;

        private readonly byte[] _handle;

        private readonly Queue<uint> _pending = new Queue<uint>();

        private long _position;

        private bool _closed;

        public WriteStream(SftpChannel sftp, byte[] handle, long offset)
        {
            _sftp = sftp;
            _handle = handle;
            _position = offset;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int chunk = Math.Min(count, ChunkSize);
                PacketWriter request = new PacketWriter(FxpWrite);
                request.WriteBytes(_handle);
                request.WriteUInt64((ulong)_position);
                request.WriteBytes(buffer, offset, chunk);
                _pending.Enqueue(_sftp.Send(request));
                _position += chunk;
                offset += chunk;
                count -= chunk;
                while (_pending.Count >= MaxRequestsInFlight)
                {
                    _sftp.CheckStatusReply(_pending.Dequeue(), null);
                }
            }
        }

        /// <summary>
        /// Waits until the server has confirmed every write sent so far.
        /// </summary>
        public override void Flush()
        {
            while (_pending.Count != 0)
            {
                _sftp.CheckStatusReply(_pending.Dequeue(), null);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                try
                {
                    Flush();
                }
                finally
                {
                    _sftp.CloseHandle(_handle);
                }
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}
