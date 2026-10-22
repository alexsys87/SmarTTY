using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SmarTTY.Ssh;

/// <summary>
/// An SSH session channel running a shell or a command, optionally with a PTY.
/// Received data is either buffered for the blocking <see cref="Output"/>/<see cref="Error"/> streams
/// or, when <see cref="PollingEnabled"/> is set, delivered through <see cref="DataReceived"/>
/// on the session's event thread.
/// </summary>
public class SshShellChannel : IAsyncChannel, IDisposable
{
    public class ChannelStream : Stream
    {
        private readonly SshShellChannel _channel;

        private readonly int _streamId;

        private readonly bool _canWrite;

        public bool ReadFully;

        public override bool CanRead => !_canWrite;

        public override bool CanSeek => false;

        public override bool CanWrite => _canWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal ChannelStream(SshShellChannel channel, int streamId, bool canWrite)
        {
            _channel = channel;
            _streamId = streamId;
            _canWrite = canWrite;
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!ReadFully)
            {
                return _channel.ReadStream(_streamId, buffer, offset, count);
            }
            int done = 0;
            while (done < count)
            {
                int read = _channel.ReadStream(_streamId, buffer, offset + done, count - done);
                if (read <= 0)
                {
                    break;
                }
                done += read;
            }
            return done;
        }

        // Any stream of the channel writes to the remote stdin (the old API allowed writing via Output too).
        public override void Write(byte[] buffer, int offset, int count)
        {
            _channel.WriteData(buffer, offset, count);
        }
    }

    /// <summary>
    /// FIFO of received chunks for one stream (stdout or stderr).
    /// </summary>
    private class ChunkQueue
    {
        private readonly Queue<byte[]> _chunks = new Queue<byte[]>();

        private int _offset;

        public bool IsEmpty => _chunks.Count == 0;

        public void Add(byte[] chunk)
        {
            _chunks.Enqueue(chunk);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            byte[] chunk = _chunks.Peek();
            int done = Math.Min(count, chunk.Length - _offset);
            Buffer.BlockCopy(chunk, _offset, buffer, offset, done);
            _offset += done;
            if (_offset == chunk.Length)
            {
                _chunks.Dequeue();
                _offset = 0;
            }
            return done;
        }

        public byte[] TakeChunk()
        {
            byte[] chunk = _chunks.Dequeue();
            if (_offset != 0)
            {
                byte[] rest = new byte[chunk.Length - _offset];
                Buffer.BlockCopy(chunk, _offset, rest, 0, rest.Length);
                chunk = rest;
                _offset = 0;
            }
            return chunk;
        }
    }

    private const int StdoutStreamId = 0;

    private const int StderrStreamId = 1;

    // How long ExitStatus waits for the exit-status message once the remote side has sent EOF.
    private static readonly TimeSpan ExitStatusTimeout = TimeSpan.FromSeconds(10);

    private readonly SshSession _session;

    private readonly SshNetChannel _channel;

    private readonly object _sync = new object();

    private readonly ChunkQueue[] _queues = new ChunkQueue[2] { new ChunkQueue(), new ChunkQueue() };

    private ExtendedDataMode _extendedDataMode;

    private bool _polling;

    private bool _eofReceived;

    private bool _closed;

    private bool _eofSent;

    private bool _disposed;

    private int _finishedRaised;

    private bool _closedByConnectionLoss;

    private int? _exitStatus;

    private string _exitSignal;

    public readonly ChannelStream Input;

    public readonly ChannelStream Output;

    public readonly ChannelStream Error;

    public readonly string TerminalType;

    public SshSession Session => _session;

    public bool PollingEnabled
    {
        get
        {
            lock (_sync)
            {
                return _polling;
            }
        }
        set
        {
            lock (_sync)
            {
                if (_polling == value)
                {
                    return;
                }
                _polling = value;
                if (!value)
                {
                    return;
                }
                // Hand the data buffered so far to the event handlers, then continue with live data.
                for (int streamId = 0; streamId < _queues.Length; streamId++)
                {
                    while (!_queues[streamId].IsEmpty)
                    {
                        DispatchData(_queues[streamId].TakeChunk(), streamId);
                    }
                }
                if (_closed)
                {
                    DispatchFinished();
                }
            }
        }
    }

    public ExtendedDataMode ExtendedDataMode
    {
        get
        {
            lock (_sync)
            {
                return _extendedDataMode;
            }
        }
        set
        {
            lock (_sync)
            {
                _extendedDataMode = value;
            }
        }
    }

    /// <summary>
    /// When set, reading <see cref="Output"/> also delivers pending stderr data via <see cref="DataReceived"/>
    /// on the reading thread, so stderr does not get stuck while the caller consumes stdout.
    /// </summary>
    public bool ReadStderrOnRead { get; set; }

    /// <summary>
    /// Exit code of the remote command; -1 if it was terminated by a signal, 0 if the server reported nothing.
    /// </summary>
    public int ExitStatus
    {
        get
        {
            lock (_sync)
            {
                // exit-status may arrive slightly after EOF; give it a chance before reporting.
                DateTime deadline = DateTime.UtcNow + ExitStatusTimeout;
                while (_eofReceived && !_closed && _exitStatus == null && _exitSignal == null)
                {
                    TimeSpan remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }
                    Monitor.Wait(_sync, remaining);
                }
                if (_exitStatus.HasValue)
                {
                    return _exitStatus.Value;
                }
                return (_exitSignal != null) ? (-1) : 0;
            }
        }
    }

    public bool IsEof
    {
        get
        {
            lock (_sync)
            {
                return _eofReceived && _queues[StdoutStreamId].IsEmpty && _queues[StderrStreamId].IsEmpty;
            }
        }
    }

    /// <summary>
    /// True when the channel was still open at the moment the SSH connection went away (or the session was
    /// disposed): the remote program did not exit, so <see cref="Finished"/> does not mean "the shell has exited".
    /// </summary>
    public bool ClosedByConnectionLoss
    {
        get
        {
            lock (_sync)
            {
                return _closedByConnectionLoss;
            }
        }
    }

    /// <summary>True once <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    public event DataReceivedHandler DataReceived;

    public event ChannelEventHandler Finished;

    /// <summary>
    /// Opens a session channel and starts <paramref name="command"/> (or a shell if it is null),
    /// requesting a PTY of type <paramref name="terminalType"/> if it is not null.
    /// </summary>
    internal SshShellChannel(SshSession session, SshNetChannel channel, string command, string terminalType, ExtendedDataMode extendedDataMode)
        : this(session, channel, command, terminalType, extendedDataMode, subsystem: null)
    {
    }

    /// <summary>
    /// Opens a session channel for the <paramref name="subsystem"/> (e.g. "sftp") when it is not null, otherwise as above.
    /// </summary>
    internal SshShellChannel(SshSession session, SshNetChannel channel, string command, string terminalType, ExtendedDataMode extendedDataMode, string subsystem)
    {
        _session = session;
        _channel = channel;
        _extendedDataMode = extendedDataMode;
        TerminalType = terminalType;
        Input = new ChannelStream(this, StdoutStreamId, canWrite: true);
        Output = new ChannelStream(this, StdoutStreamId, canWrite: false);
        Error = new ChannelStream(this, StderrStreamId, canWrite: false);
        _channel.Subscribe(OnChannelData, OnChannelExtendedData, OnChannelEof, OnChannelClosed, OnExitStatus, OnExitSignal, OnChannelException);
        try
        {
            _session.RegisterChannel(this);
            bool started;
            if (terminalType != null)
            {
                _session.PtyGate.BeginPtySetup();
            }
            try
            {
                _channel.Open();
                if (terminalType != null && !_channel.SendPseudoTerminalRequest(terminalType, 80, 25))
                {
                    throw new PtyRequestFailedException();
                }
                if (subsystem != null)
                {
                    started = _channel.SendSubsystemRequest(subsystem);
                }
                else
                {
                    started = (command != null) ? _channel.SendExecRequest(command) : _channel.SendShellRequest();
                }
            }
            finally
            {
                if (terminalType != null)
                {
                    _session.PtyGate.EndPtySetup();
                }
            }
            if (!started)
            {
                throw new RemoteOperationException((subsystem != null) ? ("The server does not provide the " + subsystem + " subsystem") : (command != null) ? ("The server refused to run '" + command + "'") : "The server refused to start a shell");
            }
        }
        catch
        {
            _session.UnregisterChannel(this);
            _channel.Dispose();
            throw;
        }
    }

    internal void OnConnectionClosed()
    {
        lock (_sync)
        {
            if (!_closed)
            {
                _closedByConnectionLoss = true;
            }
        }
        MarkClosed();
    }

    private void OnChannelData(ArraySegment<byte> data)
    {
        OnData(StdoutStreamId, data);
    }

    private void OnChannelExtendedData(ArraySegment<byte> data)
    {
        OnData(StderrStreamId, data);
    }

    // Called on the SSH.NET receive thread; must not block.
    private void OnData(int streamId, ArraySegment<byte> data)
    {
        // The segment may point into a reused receive buffer, so it is copied right away.
        byte[] chunk = new byte[data.Count];
        Buffer.BlockCopy(data.Array, data.Offset, chunk, 0, data.Count);
        _session.AddReceivedBytes(chunk.Length);
        lock (_sync)
        {
            if (streamId == StderrStreamId)
            {
                if (_extendedDataMode == ExtendedDataMode.Ignore)
                {
                    return;
                }
                if (_extendedDataMode == ExtendedDataMode.Merge)
                {
                    streamId = StdoutStreamId;
                }
            }
            if (_polling)
            {
                DispatchData(chunk, streamId);
                return;
            }
            _queues[streamId].Add(chunk);
            Monitor.PulseAll(_sync);
        }
    }

    private void OnChannelEof()
    {
        lock (_sync)
        {
            _eofReceived = true;
            Monitor.PulseAll(_sync);
        }
    }

    private void OnChannelClosed()
    {
        MarkClosed();
    }

    private void OnExitStatus(int exitStatus)
    {
        lock (_sync)
        {
            _exitStatus = exitStatus;
            Monitor.PulseAll(_sync);
        }
    }

    private void OnExitSignal(string signalName)
    {
        lock (_sync)
        {
            _exitSignal = signalName;
            Monitor.PulseAll(_sync);
        }
    }

    private void OnChannelException(Exception exception)
    {
        MarkClosed();
    }

    private void MarkClosed()
    {
        lock (_sync)
        {
            _eofReceived = true;
            _closed = true;
            Monitor.PulseAll(_sync);
            if (_polling)
            {
                DispatchFinished();
            }
        }
    }

    // Must be called under _sync so that events keep the order in which the data arrived.
    private void DispatchData(byte[] chunk, int streamId)
    {
        _session.Dispatch(() =>
        {
            DataReceived?.Invoke(this, chunk, 0, chunk.Length, streamId);
        });
    }

    private void DispatchFinished()
    {
        _session.Dispatch(RaiseFinished);
    }

    private void RaiseFinished()
    {
        if (Interlocked.Exchange(ref _finishedRaised, 1) == 0)
        {
            Finished?.Invoke(this);
        }
    }

    private int ReadStream(int streamId, byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            return 0;
        }
        while (true)
        {
            byte[] stderrChunk = null;
            lock (_sync)
            {
                if (streamId == StdoutStreamId && ReadStderrOnRead && !_queues[StderrStreamId].IsEmpty)
                {
                    stderrChunk = _queues[StderrStreamId].TakeChunk();
                }
                else if (!_queues[streamId].IsEmpty)
                {
                    return _queues[streamId].Read(buffer, offset, count);
                }
                else if (_eofReceived)
                {
                    return 0;
                }
                else
                {
                    Monitor.Wait(_sync);
                    continue;
                }
            }
            DataReceived?.Invoke(this, stderrChunk, 0, stderrChunk.Length, StderrStreamId);
        }
    }

    private void WriteData(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            if (_closed || _eofSent)
            {
                throw new IOException("Cannot write to a closed SSH channel");
            }
        }
        _channel.SendData(buffer, offset, count);
        _session.AddSentBytes(count);
    }

    public void ResizePty(int columns, int rows)
    {
        _channel.SendWindowChangeRequest((uint)Math.Max(columns, 1), (uint)Math.Max(rows, 1));
    }

    public void Flush(int streamId)
    {
    }

    public void SendEof()
    {
        lock (_sync)
        {
            if (_eofSent || _closed)
            {
                return;
            }
            _eofSent = true;
        }
        _channel.SendEof();
    }

    public void WaitEof()
    {
        lock (_sync)
        {
            while (!_eofReceived)
            {
                Monitor.Wait(_sync);
            }
        }
    }

    public void WaitClosed()
    {
        lock (_sync)
        {
            while (!_closed)
            {
                Monitor.Wait(_sync);
            }
        }
    }

    public bool TryWaitClosed()
    {
        lock (_sync)
        {
            return _closed;
        }
    }

    public virtual void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _polling = false;
        }
        _session.UnregisterChannel(this);
        try
        {
            _channel.Dispose();
        }
        catch
        {
        }
        lock (_sync)
        {
            _eofReceived = true;
            _closed = true;
            Monitor.PulseAll(_sync);
        }
        RaiseFinished();
    }
}
