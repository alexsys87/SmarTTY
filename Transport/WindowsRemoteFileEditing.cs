using SmarTTY.Ssh;
using System;
using System.IO;

namespace SmarTTY.Transport;

/// <summary>
/// What the remote file editor does with scp, test, stat, cat, cp and rm for a Windows host, over SFTP: reading the file,
/// telling whether it exists, noticing that somebody else changed it, and saving.
/// </summary>
internal static class WindowsRemoteFileEditing
{
    /// <summary>
    /// Identifies the version of a file on the server: modification time in seconds and size, like "stat -c '%Y %s'" of
    /// the Unix side. Null when there is no such file.
    /// </summary>
    public static string Signature(SftpChannel sftp, string path)
    {
        SftpFileAttributes attributes = sftp.Stat(path);
        if (attributes == null)
        {
            return null;
        }
        long seconds = (long)((attributes.ModifyTime ?? SshSession.UnixEpoch) - SshSession.UnixEpoch).TotalSeconds;
        return seconds + " " + (attributes.Size ?? 0);
    }

    public static string Signature(SshSession session, string path)
    {
        using SftpChannel sftp = SftpChannel.Open(session);
        return Signature(sftp, path);
    }

    public static bool Exists(SshSession session, string path)
    {
        using SftpChannel sftp = SftpChannel.Open(session);
        return sftp.Lstat(path) != null;
    }

    /// <summary>
    /// Writes the bytes to the file, which is created or truncated in place: that keeps its owner and its access rights,
    /// where a replacement through a temporary file would give it those of the folder. With <paramref name="backup"/> the old
    /// file is copied to "file~" first; a backup that cannot be made does not stop the save (as with other editors). The size
    /// is checked afterwards, because a write that stopped short must not pass for a save.
    /// </summary>
    public static void Save(SftpChannel sftp, string path, byte[] bytes, bool backup)
    {
        if (backup && sftp.Lstat(path) != null)
        {
            try
            {
                new SftpFileSystem(sftp).Copy(path, path + "~", replace: true);
            }
            catch (Exception exception) when (exception is RemoteOperationException || exception is SftpStatusException)
            {
            }
        }
        using (Stream stream = sftp.OpenWrite(path, 0, truncate: true))
        {
            stream.Write(bytes, 0, bytes.Length);
        }
        long? size = sftp.Stat(path)?.Size;
        if (size != bytes.Length)
        {
            throw new RemoteOperationException("Only " + (size ?? 0) + " of " + bytes.Length + " bytes of " + path + " were written.");
        }
    }

    public static void Save(SshSession session, string path, byte[] bytes, bool backup)
    {
        using SftpChannel sftp = SftpChannel.Open(session);
        Save(sftp, path, bytes, backup);
    }

    /// <summary>The file as a stream, and its size; the stream owns the SFTP channel and closes it when it is disposed.</summary>
    public static Stream OpenRead(SshSession session, string path, out long length)
    {
        SftpChannel sftp = SftpChannel.Open(session);
        try
        {
            SftpFileAttributes attributes = sftp.Stat(path) ?? throw new SftpStatusException(SftpChannel.StatusNoSuchFile, path + ": No such file");
            length = attributes.Size ?? 0;
            return new ChannelOwningStream(sftp.OpenRead(path, 0), sftp);
        }
        catch
        {
            sftp.Dispose();
            throw;
        }
    }

    /// <summary>A read-only stream that disposes what it was made from together with itself.</summary>
    private sealed class ChannelOwningStream : Stream
    {
        private readonly Stream _inner;

        private readonly IDisposable _owner;

        public ChannelOwningStream(Stream inner, IDisposable owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _owner.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
