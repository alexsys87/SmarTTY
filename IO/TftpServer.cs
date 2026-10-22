using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SmarTTY.IO;

public enum TftpTransferState
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>One file being sent to one client. The server updates it; read it from the events or any thread.</summary>
public sealed class TftpTransfer
{
    private long _bytesAcknowledged;
    private volatile TftpTransferState _state = TftpTransferState.Running;
    private volatile string _error;

    internal Socket Socket;
    internal volatile bool CancelRequested;

    public int Id { get; internal set; }

    public IPEndPoint Client { get; internal set; }

    /// <summary>The name the client asked for, as the server knows the file.</summary>
    public string FileName { get; internal set; }

    public string FilePath { get; internal set; }

    public long Length { get; internal set; }

    public int BlockSize { get; internal set; } = 512;

    public int WindowSize { get; internal set; } = 1;

    public DateTime StartedUtc { get; internal set; }

    public DateTime EndedUtc { get; internal set; }

    /// <summary>How much of the file the client has confirmed so far.</summary>
    public long BytesAcknowledged
    {
        get => Interlocked.Read(ref _bytesAcknowledged);
        internal set => Interlocked.Exchange(ref _bytesAcknowledged, value);
    }

    public TftpTransferState State
    {
        get => _state;
        internal set => _state = value;
    }

    /// <summary>Why a failed transfer failed; null otherwise.</summary>
    public string Error
    {
        get => _error;
        internal set => _error = value;
    }

    public bool Succeeded => _state == TftpTransferState.Completed;
}

/// <summary>
/// A small read-only TFTP server (RFC 1350 with the option extensions of RFC 2347 / 2348 / 2349 / 7440: blksize, tsize,
/// timeout, windowsize) to give firmware to a boot loader such as U-Boot. Only the files registered with
/// <see cref="AddFile"/> are served, by exactly the name they were registered with: there is no directory access, so a
/// client cannot ask for anything else. Writes are refused. Every transfer runs on its own thread and its own UDP port, as
/// the protocol requires.
///
/// The events are raised on worker threads: a handler that touches the user interface must marshal the call itself.
/// </summary>
public sealed class TftpServer : IDisposable
{
    private const int OpRrq = 1;
    private const int OpWrq = 2;
    private const int OpData = 3;
    private const int OpAck = 4;
    private const int OpError = 5;
    private const int OpOack = 6;

    private const int ErrNotDefined = 0;
    private const int ErrFileNotFound = 1;
    private const int ErrAccessViolation = 2;
    private const int ErrIllegalOperation = 4;
    private const int ErrUnknownTransferId = 5;

    private const int MinBlockSize = 8;
    private const int MaxProtocolBlockSize = 65464;
    private const int MaxConcurrentTransfers = 16;

    private readonly IPAddress _bindAddress;
    private readonly int _port;
    private readonly object _lock = new object();
    private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<TftpTransfer> _active = new List<TftpTransfer>();

    private Socket _listener;
    private Thread _listenerThread;
    private volatile bool _running;
    private bool _packetInfo;
    private int _nextTransferId;

    public TftpServer(IPAddress bindAddress, int port = 69)
    {
        _bindAddress = bindAddress ?? IPAddress.Any;
        _port = port;
    }

    /// <summary>When set, requests from any other address are ignored (without an answer).</summary>
    public IPAddress AllowedClient { get; set; }

    /// <summary>How often a packet is sent again before the transfer is given up.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>The wait for an answer when the client did not negotiate a timeout.</summary>
    public int DefaultTimeoutMs { get; set; } = 2000;

    /// <summary>The largest block size the server accepts when the client asks for a bigger one.</summary>
    public int MaxBlockSize { get; set; } = MaxProtocolBlockSize;

    /// <summary>The largest window (blocks sent before waiting for an acknowledgement) the server accepts.</summary>
    public int MaxWindowSize { get; set; } = 64;

    public bool IsRunning => _running;

    /// <summary>The address and port the server listens on; valid while it is running (the real port when 0 was asked for).</summary>
    public IPEndPoint LocalEndPoint { get; private set; }

    public int ActiveTransferCount
    {
        get
        {
            lock (_lock)
            {
                return _active.Count;
            }
        }
    }

    public event Action<string> Log;

    public event Action<TftpTransfer> TransferStarted;

    /// <summary>Raised while a transfer runs, at most every 100 ms.</summary>
    public event Action<TftpTransfer> TransferProgress;

    /// <summary>Raised once when a transfer ends, whatever the outcome (see <see cref="TftpTransfer.State"/>).</summary>
    public event Action<TftpTransfer> TransferEnded;

    // ---------- served files ----------

    /// <summary>
    /// Makes <paramref name="path"/> available under <paramref name="servedName"/>. Names are compared without regard to
    /// case, and a leading "/" or "./" in a request is ignored. Registering a name again replaces its file.
    /// </summary>
    public void AddFile(string servedName, string path)
    {
        string key = NormalizeName(servedName);
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("The served name is empty.", nameof(servedName));
        }
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("The file path is empty.", nameof(path));
        }
        lock (_lock)
        {
            _files[key] = path;
            _displayNames[key] = servedName;
        }
    }

    public void ClearFiles()
    {
        lock (_lock)
        {
            _files.Clear();
            _displayNames.Clear();
        }
    }

    /// <summary>The registered files: served name and path.</summary>
    public KeyValuePair<string, string>[] GetFiles()
    {
        lock (_lock)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> pair in _files)
            {
                list.Add(new KeyValuePair<string, string>(_displayNames[pair.Key], pair.Value));
            }
            return list.ToArray();
        }
    }

    internal static string NormalizeName(string name)
    {
        if (name == null)
        {
            return null;
        }
        string n = name.Trim().Replace('\\', '/');
        while (n.StartsWith("./", StringComparison.Ordinal))
        {
            n = n.Substring(2);
        }
        return n.TrimStart('/');
    }

    // ---------- start and stop ----------

    /// <summary>Starts listening. Throws a <see cref="SocketException"/> when the port cannot be used (taken by another TFTP server, for instance).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_running)
            {
                return;
            }

            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                DisableConnectionReset(socket);
                try
                {
                    // Nobody else may share the port with the server.
                    socket.ExclusiveAddressUse = true;
                }
                catch (Exception)
                {
                }
                _packetInfo = false;
                if (_bindAddress.Equals(IPAddress.Any))
                {
                    try
                    {
                        // Lets a transfer answer from the address the request was sent to on a computer with several.
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                        _packetInfo = true;
                    }
                    catch (Exception)
                    {
                    }
                }
                socket.Bind(new IPEndPoint(_bindAddress, _port));
            }
            catch
            {
                socket.Close();
                throw;
            }

            _listener = socket;
            LocalEndPoint = (IPEndPoint)socket.LocalEndPoint;
            _running = true;
            _listenerThread = new Thread(() => ListenLoop(socket)) { IsBackground = true, Name = "TFTP server" };
            _listenerThread.Start();
        }
        RaiseLog("TFTP server listening on " + LocalEndPoint + ".");
    }

    /// <summary>Stops listening and cancels the transfers in progress.</summary>
    public void Stop()
    {
        Thread listenerThread;
        List<TftpTransfer> toCancel;
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }
            _running = false;
            try
            {
                _listener?.Close();
            }
            catch (Exception)
            {
            }
            _listener = null;
            listenerThread = _listenerThread;
            _listenerThread = null;
            toCancel = new List<TftpTransfer>(_active);
        }

        foreach (TftpTransfer transfer in toCancel)
        {
            transfer.CancelRequested = true;
            try
            {
                transfer.Socket?.Close();
            }
            catch (Exception)
            {
            }
        }
        if (listenerThread != null && listenerThread != Thread.CurrentThread)
        {
            listenerThread.Join(2000);
        }
        // The transfer threads end by themselves; give them a moment so their events are out before Stop returns.
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (ActiveTransferCount > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
        RaiseLog("TFTP server stopped.");
    }

    public void Dispose()
    {
        Stop();
    }

    // ---------- requests ----------

    private void ListenLoop(Socket socket)
    {
        byte[] buffer = new byte[4096];
        while (_running)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int length;
            IPAddress local = null;
            try
            {
                if (_packetInfo)
                {
                    SocketFlags flags = SocketFlags.None;
                    length = socket.ReceiveMessageFrom(buffer, 0, buffer.Length, ref flags, ref remote, out IPPacketInformation info);
                    local = info.Address;
                }
                else
                {
                    length = socket.ReceiveFrom(buffer, ref remote);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (!_running)
                {
                    break;
                }
                // An earlier answer bounced (the client is gone): nothing to do with the next request.
                if (ex.SocketErrorCode != SocketError.ConnectionReset)
                {
                    RaiseLog("TFTP receive error: " + ex.Message);
                    Thread.Sleep(50);
                }
                continue;
            }

            try
            {
                HandleRequest(buffer, length, (IPEndPoint)remote, local ?? (_bindAddress.Equals(IPAddress.Any) ? null : _bindAddress));
            }
            catch (Exception ex)
            {
                RaiseLog("TFTP request error: " + ex.Message);
            }
        }
    }

    private void HandleRequest(byte[] packet, int length, IPEndPoint client, IPAddress local)
    {
        if (length < 4)
        {
            return;
        }
        int opcode = (packet[0] << 8) | packet[1];
        if (opcode != OpRrq && opcode != OpWrq)
        {
            return; // a stray packet to the well-known port
        }

        IPAddress allowed = AllowedClient;
        if (allowed != null && !allowed.Equals(client.Address))
        {
            RaiseLog("Ignored a request from " + client.Address + " (only " + allowed + " is allowed).");
            return;
        }

        if (!TryParseRequest(packet, length, out string fileName, out string mode, out List<KeyValuePair<string, string>> options))
        {
            ReplyError(local, client, ErrIllegalOperation, "Malformed request.");
            return;
        }

        if (opcode == OpWrq)
        {
            RaiseLog("Refused a write request for \"" + fileName + "\" from " + client + " (the server is read-only).");
            ReplyError(local, client, ErrAccessViolation, "This server is read-only.");
            return;
        }

        if (!string.Equals(mode, "octet", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "binary", StringComparison.OrdinalIgnoreCase))
        {
            ReplyError(local, client, ErrIllegalOperation, "Only octet (binary) mode is supported.");
            return;
        }

        string key = NormalizeName(fileName);
        string path;
        string servedName = null;
        bool duplicate = false;
        bool tooMany = false;
        lock (_lock)
        {
            if (!_files.TryGetValue(key ?? string.Empty, out path))
            {
                path = null;
            }
            else
            {
                servedName = _displayNames[key];
            }
            foreach (TftpTransfer other in _active)
            {
                if (other.Client.Equals(client))
                {
                    duplicate = true; // the client repeated its request while the first answer was on its way
                    break;
                }
            }
            tooMany = path != null && !duplicate && _active.Count >= MaxConcurrentTransfers;
        }
        if (duplicate)
        {
            return;
        }
        if (tooMany)
        {
            RaiseLog("Refused \"" + fileName + "\" for " + client + ": too many transfers.");
            ReplyError(local, client, ErrNotDefined, "Too many transfers in progress.");
            return;
        }
        if (path == null)
        {
            RaiseLog("Request for \"" + fileName + "\" from " + client + ": no such file is served.");
            ReplyError(local, client, ErrFileNotFound, "File not found.");
            return;
        }

        FileStream stream;
        long fileLength;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fileLength = stream.Length;
        }
        catch (Exception ex)
        {
            RaiseLog("Cannot read \"" + path + "\": " + ex.Message);
            ReplyError(local, client, ErrAccessViolation, "The file cannot be read.");
            return;
        }

        Socket socket;
        try
        {
            socket = CreateTransferSocket(local);
        }
        catch (Exception ex)
        {
            stream.Dispose();
            RaiseLog("Cannot open a transfer socket: " + ex.Message);
            return;
        }

        TftpTransfer transfer = new TftpTransfer
        {
            Id = Interlocked.Increment(ref _nextTransferId),
            Client = client,
            FileName = servedName,
            FilePath = path,
            Length = fileLength,
            StartedUtc = DateTime.UtcNow,
            Socket = socket
        };

        NegotiateOptions(options, transfer, out byte[] oack, out int timeoutMs);

        lock (_lock)
        {
            if (!_running)
            {
                socket.Close();
                stream.Dispose();
                return;
            }
            _active.Add(transfer);
        }

        Thread thread = new Thread(() => RunTransfer(transfer, stream, socket, oack, timeoutMs)) { IsBackground = true, Name = "TFTP transfer " + transfer.Id };
        thread.Start();
    }

    /// <summary>Splits a request into the file name, the mode and the options. False when it is malformed.</summary>
    internal static bool TryParseRequest(byte[] packet, int length, out string fileName, out string mode, out List<KeyValuePair<string, string>> options)
    {
        fileName = null;
        mode = null;
        options = new List<KeyValuePair<string, string>>();
        List<string> fields = new List<string>();
        int pos = 2;
        while (pos < length)
        {
            int end = Array.IndexOf(packet, (byte)0, pos, length - pos);
            if (end < 0)
            {
                return false; // the last field is not terminated
            }
            fields.Add(Encoding.UTF8.GetString(packet, pos, end - pos));
            pos = end + 1;
        }
        if (fields.Count < 2 || fields.Count % 2 != 0 || fields[0].Length == 0)
        {
            return false;
        }
        fileName = fields[0];
        mode = fields[1];
        for (int i = 2; i + 1 < fields.Count; i += 2)
        {
            options.Add(new KeyValuePair<string, string>(fields[i].ToLowerInvariant(), fields[i + 1]));
        }
        return true;
    }

    /// <summary>
    /// Settles the options the client asked for. Fills the transfer's block and window size, returns the acknowledgement
    /// to send (null when the client asked for no option the server knows) and the wait for answers.
    /// </summary>
    private void NegotiateOptions(List<KeyValuePair<string, string>> requested, TftpTransfer transfer, out byte[] oack, out int timeoutMs)
    {
        timeoutMs = Math.Max(100, DefaultTimeoutMs);
        List<KeyValuePair<string, string>> accepted = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> option in requested)
        {
            if (!long.TryParse(option.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                continue;
            }
            switch (option.Key)
            {
                case "blksize":
                    if (value >= MinBlockSize && value <= MaxProtocolBlockSize)
                    {
                        transfer.BlockSize = (int)Math.Min(value, Math.Min(MaxBlockSize, MaxProtocolBlockSize));
                        accepted.Add(new KeyValuePair<string, string>("blksize", transfer.BlockSize.ToString(CultureInfo.InvariantCulture)));
                    }
                    break;
                case "tsize":
                    if (value == 0) // the client asks how big the file is
                    {
                        accepted.Add(new KeyValuePair<string, string>("tsize", transfer.Length.ToString(CultureInfo.InvariantCulture)));
                    }
                    break;
                case "timeout":
                    if (value >= 1 && value <= 255)
                    {
                        timeoutMs = (int)value * 1000;
                        accepted.Add(new KeyValuePair<string, string>("timeout", value.ToString(CultureInfo.InvariantCulture)));
                    }
                    break;
                case "windowsize":
                    if (value >= 1 && value <= 65535)
                    {
                        transfer.WindowSize = (int)Math.Min(value, Math.Max(1, MaxWindowSize));
                        accepted.Add(new KeyValuePair<string, string>("windowsize", transfer.WindowSize.ToString(CultureInfo.InvariantCulture)));
                    }
                    break;
            }
        }

        if (accepted.Count == 0)
        {
            oack = null;
            return;
        }
        using MemoryStream stream = new MemoryStream();
        stream.WriteByte(0);
        stream.WriteByte(OpOack);
        foreach (KeyValuePair<string, string> option in accepted)
        {
            byte[] name = Encoding.ASCII.GetBytes(option.Key);
            byte[] value = Encoding.ASCII.GetBytes(option.Value);
            stream.Write(name, 0, name.Length);
            stream.WriteByte(0);
            stream.Write(value, 0, value.Length);
            stream.WriteByte(0);
        }
        oack = stream.ToArray();
    }

    // ---------- transfers ----------

    private sealed class TransferFailure : Exception
    {
        public TransferFailure(string message) : base(message)
        {
        }
    }

    private void RunTransfer(TftpTransfer transfer, FileStream stream, Socket socket, byte[] oack, int timeoutMs)
    {
        string description = "\"" + transfer.FileName + "\" to " + transfer.Client.Address + ":" + transfer.Client.Port;
        RaiseLog("Sending " + description + " (" + transfer.Length.ToString(CultureInfo.InvariantCulture) + " bytes, block size "
            + transfer.BlockSize + (transfer.WindowSize > 1 ? ", window " + transfer.WindowSize : string.Empty) + ").");
        RaiseEvent(TransferStarted, transfer);

        try
        {
            SendFile(transfer, stream, socket, oack, timeoutMs);
            transfer.State = TftpTransferState.Completed;
        }
        catch (OperationCanceledException)
        {
            transfer.State = TftpTransferState.Cancelled;
            transfer.Error = "Cancelled.";
        }
        catch (TransferFailure ex)
        {
            transfer.State = transfer.CancelRequested ? TftpTransferState.Cancelled : TftpTransferState.Failed;
            transfer.Error = ex.Message;
        }
        catch (Exception ex)
        {
            transfer.State = transfer.CancelRequested ? TftpTransferState.Cancelled : TftpTransferState.Failed;
            transfer.Error = ex.Message;
        }
        finally
        {
            transfer.EndedUtc = DateTime.UtcNow;
            try
            {
                socket.Close();
            }
            catch (Exception)
            {
            }
            stream.Dispose();
        }

        if (transfer.State == TftpTransferState.Completed)
        {
            double seconds = Math.Max(0.001, (transfer.EndedUtc - transfer.StartedUtc).TotalSeconds);
            RaiseLog("Sent " + description + ": " + transfer.Length.ToString(CultureInfo.InvariantCulture) + " bytes in "
                + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s ("
                + (transfer.Length / seconds / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB/s).");
        }
        else if (transfer.State == TftpTransferState.Cancelled)
        {
            RaiseLog("Cancelled " + description + ".");
        }
        else
        {
            RaiseLog("Failed " + description + ": " + transfer.Error);
        }

        lock (_lock)
        {
            _active.Remove(transfer);
        }
        RaiseEvent(TransferEnded, transfer);
    }

    private void SendFile(TftpTransfer transfer, FileStream stream, Socket socket, byte[] oack, int timeoutMs)
    {
        IPEndPoint client = transfer.Client;
        int blockSize = transfer.BlockSize;
        int window = transfer.WindowSize;
        long lastBlock = transfer.Length / blockSize + 1; // the last block is short or empty, which tells the client it is the last
        byte[] packet = new byte[4 + blockSize];
        byte[] incoming = new byte[2048];
        int retries = 0;
        int maxRetries = Math.Max(1, MaxRetries);
        long streamPosition = 0;
        int lastProgress = Environment.TickCount;

        // An accepted option is confirmed first; the client answers with an acknowledgement of block 0.
        if (oack != null)
        {
            while (true)
            {
                ThrowIfCancelled(transfer);
                socket.SendTo(oack, client);
                int n = Receive(transfer, socket, incoming, timeoutMs, out int opcode);
                if (n < 0)
                {
                    if (++retries > maxRetries)
                    {
                        throw new TransferFailure("The client did not answer.");
                    }
                    continue;
                }
                if (opcode == OpError)
                {
                    throw new TransferFailure("The client refused the options" + ErrorText(incoming, n) + ".");
                }
                if (opcode == OpAck && n >= 4 && ((incoming[2] << 8) | incoming[3]) == 0)
                {
                    break;
                }
            }
            retries = 0;
        }

        long acked = 0;
        long next = 1;
        long gapResendFor = -1; // the acknowledged block for which the window was already sent again after a reported gap
        while (acked < lastBlock)
        {
            ThrowIfCancelled(transfer);

            while (next <= lastBlock && next - acked <= window)
            {
                long offset = (next - 1) * blockSize;
                if (streamPosition != offset)
                {
                    stream.Position = offset;
                    streamPosition = offset;
                }
                int count = 0;
                int want = (int)Math.Min(blockSize, transfer.Length - offset);
                while (count < want)
                {
                    int read = stream.Read(packet, 4 + count, want - count);
                    if (read <= 0)
                    {
                        throw new TransferFailure("The file got shorter while it was being sent.");
                    }
                    count += read;
                }
                streamPosition += count;
                packet[0] = 0;
                packet[1] = OpData;
                packet[2] = (byte)((next >> 8) & 0xFF);
                packet[3] = (byte)(next & 0xFF);
                socket.SendTo(packet, 0, 4 + count, SocketFlags.None, client);
                next++;
            }

            int received = Receive(transfer, socket, incoming, timeoutMs, out int code);
            if (received < 0)
            {
                if (++retries > maxRetries)
                {
                    throw new TransferFailure(acked >= lastBlock - 1 && acked > 0
                        ? "The last acknowledgement did not arrive (the device may have the whole file)."
                        : "Timed out waiting for the client.");
                }
                next = acked + 1; // everything not acknowledged is sent again
                continue;
            }
            if (code == OpError)
            {
                throw new TransferFailure("The client stopped the transfer" + ErrorText(incoming, received) + ".");
            }
            if (code != OpAck || received < 4)
            {
                continue;
            }

            long ack = ResolveBlock((incoming[2] << 8) | incoming[3], acked);
            if (ack > acked && ack < next)
            {
                acked = ack;
                retries = 0;
                transfer.BytesAcknowledged = Math.Min(transfer.Length, acked * blockSize);
                int now = Environment.TickCount;
                if (now - lastProgress >= 100)
                {
                    lastProgress = now;
                    RaiseEvent(TransferProgress, transfer);
                }
            }
            else if (ack == acked && window > 1 && gapResendFor != acked)
            {
                // With a window, the same acknowledgement again says the block after it did not arrive. Without one it is
                // just a late duplicate, and answering it would make every block go out twice. Every packet that arrives
                // after the gap makes the client repeat the acknowledgement: only the first one is acted upon, the
                // timeout takes care of a repeat that got lost as well.
                gapResendFor = acked;
                next = acked + 1;
            }
        }

        transfer.BytesAcknowledged = transfer.Length;
        RaiseEvent(TransferProgress, transfer);
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a packet from the client. Returns its length, or -1 on timeout.
    /// Packets from anyone else get an "unknown transfer ID" error and do not disturb the transfer.
    /// </summary>
    private static int Receive(TftpTransfer transfer, Socket socket, byte[] buffer, int timeoutMs, out int opcode)
    {
        opcode = -1;
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            ThrowIfCancelled(transfer);
            double remaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0)
            {
                return -1;
            }
            try
            {
                if (!socket.Poll((int)Math.Min(remaining, 1000000) * 1000, SelectMode.SelectRead))
                {
                    return -1;
                }
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int length = socket.ReceiveFrom(buffer, ref from);
                IPEndPoint source = (IPEndPoint)from;
                if (!source.Equals(transfer.Client))
                {
                    try
                    {
                        socket.SendTo(BuildError(ErrUnknownTransferId, "Unknown transfer ID."), source);
                    }
                    catch (SocketException)
                    {
                    }
                    continue;
                }
                if (length < 2)
                {
                    continue;
                }
                opcode = (buffer[0] << 8) | buffer[1];
                return length;
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException();
            }
            catch (SocketException ex)
            {
                if (transfer.CancelRequested)
                {
                    throw new OperationCanceledException();
                }
                // An earlier packet bounced off a client that is not listening (yet): not an error by itself.
                if (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                throw new TransferFailure("Network error: " + ex.Message);
            }
        }
    }

    private static void ThrowIfCancelled(TftpTransfer transfer)
    {
        if (transfer.CancelRequested)
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>Turns the 16-bit number on the wire into the block count, taking the one closest to <paramref name="reference"/> (blocks wrap around after 65535).</summary>
    internal static long ResolveBlock(int wire, long reference)
    {
        long n = (reference & ~0xFFFFL) | (uint)(wire & 0xFFFF);
        if (n < reference - 0x8000)
        {
            n += 0x10000;
        }
        else if (n > reference + 0x8000)
        {
            n -= 0x10000;
        }
        return n;
    }

    private static string ErrorText(byte[] packet, int length)
    {
        if (length <= 4)
        {
            return string.Empty;
        }
        int end = Array.IndexOf(packet, (byte)0, 4, length - 4);
        if (end < 0)
        {
            end = length;
        }
        string text = Encoding.UTF8.GetString(packet, 4, end - 4).Trim();
        return text.Length == 0 ? string.Empty : ": " + text;
    }

    // ---------- helpers ----------

    internal static byte[] BuildError(int code, string message)
    {
        byte[] text = Encoding.ASCII.GetBytes(message ?? string.Empty);
        byte[] packet = new byte[5 + text.Length];
        packet[1] = OpError;
        packet[2] = (byte)((code >> 8) & 0xFF);
        packet[3] = (byte)(code & 0xFF);
        Array.Copy(text, 0, packet, 4, text.Length);
        return packet;
    }

    private Socket CreateTransferSocket(IPAddress local)
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            DisableConnectionReset(socket);
            socket.Bind(new IPEndPoint(local ?? IPAddress.Any, 0));
            return socket;
        }
        catch
        {
            socket.Close();
            throw;
        }
    }

    /// <summary>The answer to a request that is refused goes out from a port of its own, like any answer.</summary>
    private void ReplyError(IPAddress local, IPEndPoint client, int code, string message)
    {
        try
        {
            using Socket socket = CreateTransferSocket(local);
            socket.SendTo(BuildError(code, message), client);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Windows reports an ICMP "port unreachable" (the client went away) as an error of the next receive on a UDP socket,
    /// which would end a transfer for nothing. This switches that off; other systems do not need it.
    /// </summary>
    private static void DisableConnectionReset(Socket socket)
    {
        try
        {
            const int SioUdpConnReset = -1744830452; // 0x9800000C
            socket.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch (Exception)
        {
        }
    }

    private void RaiseLog(string message)
    {
        RaiseEvent(Log, message);
    }

    private static void RaiseEvent<T>(Action<T> handler, T argument)
    {
        if (handler == null)
        {
            return;
        }
        try
        {
            handler(argument);
        }
        catch (Exception)
        {
            // A broken handler must not stop the server.
        }
    }
}
