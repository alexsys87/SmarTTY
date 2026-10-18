using System;
using System.IO;
using System.Text;

namespace SmarTTY.Transport;

/// <summary>
/// The Telnet layer (RFC 854 / 855) on top of an already connected stream (the TCP stream of a telnet session).
///
/// Reading removes the in-band commands from the data: the caller sees only the text of the session. The server's
/// option negotiation is answered here, so the terminal never has to know about it. Writing doubles the 0xFF bytes
/// (IAC) and ends a bare CR with NUL, as the Network Virtual Terminal requires.
///
/// Supported options (everything else is politely refused with WONT / DONT):
///   ECHO (1) and SUPPRESS-GO-AHEAD (3) - the server echoes and the session is character-at-a-time,
///   BINARY (0)                         - 8-bit clean data, CR is not followed by NUL any more,
///   TERMINAL-TYPE (24)                 - answers with the configured terminal type,
///   NAWS (31)                          - reports the terminal size, again whenever it changes.
/// By default we only answer: no option is offered on our own initiative, so a server that does not speak Telnet is
/// not confronted with unexpected commands. With <see cref="TelnetOptions.ActiveNegotiation"/> the terminal type,
/// the window size and SUPPRESS-GO-AHEAD are offered right after <see cref="Start"/>, as most telnet clients do.
///
/// One thread may read while another one writes. All the protocol state, and every write to the underlying
/// stream, is guarded by one lock, so a negotiation reply never lands in the middle of the user's data.
/// </summary>
internal sealed class TelnetStream : Stream
{
    // Commands (RFC 854).
    internal const byte SE = 240;
    internal const byte NOP = 241;
    internal const byte Break = 243;
    internal const byte InterruptProcess = 244;
    internal const byte AreYouThere = 246;
    internal const byte EraseCharacter = 247;
    internal const byte EraseLine = 248;
    internal const byte SB = 250;
    internal const byte WILL = 251;
    internal const byte WONT = 252;
    internal const byte DO = 253;
    internal const byte DONT = 254;
    internal const byte IAC = 255;

    // Options.
    internal const byte OptBinary = 0;
    internal const byte OptEcho = 1;
    internal const byte OptSuppressGoAhead = 3;
    internal const byte OptTerminalType = 24;
    internal const byte OptNaws = 31;

    // TERMINAL-TYPE subnegotiation (RFC 1091).
    private const byte TerminalTypeIs = 0;
    private const byte TerminalTypeSend = 1;

    private const byte CR = 13;
    private const byte LF = 10;
    private const byte NUL = 0;

    /// <summary>The terminal type reported when none is given; the same one the SSH tabs use.</summary>
    public const string DefaultTerminalType = "xterm";

    public const int DefaultColumns = 80;

    public const int DefaultRows = 24;

    // A subnegotiation longer than this is not something we understand; its tail is dropped.
    private const int MaxSubnegotiationLength = 256;

    private enum ParseState
    {
        Data,
        Iac,
        Will,
        Wont,
        Do,
        Dont,
        SubOption,
        SubData,
        SubIac
    }

    // Where an option stands. 'Requested' is our own offer (WILL / DO sent at start) that has not been answered
    // yet: the answer must not be answered again, or both sides would go on confirming each other forever.
    private enum OptionState : byte
    {
        Off,
        Requested,
        On
    }

    private readonly Stream _inner;

    private readonly string _terminalType;

    private readonly bool _activeNegotiation;

    // Guards everything below and every write to _inner.
    private readonly object _lock = new object();

    private readonly byte[] _readBuffer = new byte[4096];

    private readonly MemoryStream _pendingReplies = new MemoryStream();

    // The server's side of an option (WILL from it, DO from us) and our side of it (WILL from us, DO from it).
    private readonly OptionState[] _remote = new OptionState[256];

    private readonly OptionState[] _local = new OptionState[256];

    private bool _echoReported;

    private readonly byte[] _subData = new byte[MaxSubnegotiationLength];

    private ParseState _state = ParseState.Data;

    private byte _subOption;

    private int _subLength;

    private bool _previousWasCr;

    private int _columns;

    private int _rows;

    public TelnetStream(Stream inner, string terminalType = DefaultTerminalType, int columns = DefaultColumns, int rows = DefaultRows)
        : this(inner, new TelnetOptions { TerminalType = terminalType, Columns = columns, Rows = rows })
    {
    }

    public TelnetStream(Stream inner, TelnetOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        options = options ?? new TelnetOptions();
        _terminalType = string.IsNullOrWhiteSpace(options.TerminalType) ? DefaultTerminalType : options.TerminalType.Trim();
        _activeNegotiation = options.ActiveNegotiation;
        _columns = ClampSize(options.Columns, DefaultColumns);
        _rows = ClampSize(options.Rows, DefaultRows);
    }

    /// <summary>
    /// Raised on the reading thread when the server starts or stops echoing what is typed (option ECHO). A client
    /// that wants "local echo only when the server does not echo" follows this.
    /// </summary>
    public event EventHandler RemoteEchoChanged;

    /// <summary>True while the server has told us (WILL ECHO) that it echoes what we send.</summary>
    public bool RemoteEcho
    {
        get
        {
            lock (_lock)
            {
                return _remote[OptEcho] == OptionState.On;
            }
        }
    }

    /// <summary>
    /// Makes the first move when active negotiation was asked for: offers SUPPRESS-GO-AHEAD (both ways), the terminal
    /// type and the window size. Does nothing in the default, answer-only mode. Call once, right after connecting.
    /// </summary>
    public void Start()
    {
        if (!_activeNegotiation)
        {
            return;
        }
        lock (_lock)
        {
            _remote[OptSuppressGoAhead] = OptionState.Requested;
            _local[OptSuppressGoAhead] = OptionState.Requested;
            _local[OptTerminalType] = OptionState.Requested;
            _local[OptNaws] = OptionState.Requested;
            byte[] offer =
            {
                IAC, DO, OptSuppressGoAhead,
                IAC, WILL, OptSuppressGoAhead,
                IAC, WILL, OptTerminalType,
                IAC, WILL, OptNaws
            };
            _inner.Write(offer, 0, offer.Length);
        }
    }

    /// <summary>Sends a Telnet command (Break, Interrupt Process, Are You There, NOP, ...).</summary>
    public void SendCommand(byte command)
    {
        lock (_lock)
        {
            _inner.Write(new[] { IAC, command }, 0, 2);
        }
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>True while the server has asked for the terminal size (NAWS) and we agreed.</summary>
    public bool NawsEnabled
    {
        get
        {
            lock (_lock)
            {
                return _local[OptNaws] == OptionState.On;
            }
        }
    }

    /// <summary>
    /// Remembers the terminal size and, when the server asked for it (NAWS), tells the server about the new size.
    /// </summary>
    public void SetWindowSize(int columns, int rows)
    {
        lock (_lock)
        {
            _columns = ClampSize(columns, _columns);
            _rows = ClampSize(rows, _rows);
            if (_local[OptNaws] == OptionState.On)
            {
                byte[] message = BuildWindowSizeMessage();
                _inner.Write(message, 0, message.Length);
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        if (count == 0)
        {
            return 0;
        }
        while (true)
        {
            // At most 'count' bytes are taken from the connection and a byte never grows when the commands are
            // removed, so the result always fits and nothing has to be kept for the next call.
            int received = _inner.Read(_readBuffer, 0, Math.Min(count, _readBuffer.Length));
            if (received <= 0)
            {
                return 0;
            }
            int produced;
            bool echoChanged;
            lock (_lock)
            {
                produced = Process(_readBuffer, received, buffer, offset);
                FlushReplies();
                bool echoNow = _remote[OptEcho] == OptionState.On;
                echoChanged = echoNow != _echoReported;
                _echoReported = echoNow;
            }
            if (echoChanged)
            {
                // Outside the lock: the handler may call back into the stream.
                RemoteEchoChanged?.Invoke(this, EventArgs.Empty);
            }
            if (produced > 0)
            {
                return produced;
            }
            // The chunk held only commands: wait for the text that follows.
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        if (count == 0)
        {
            return;
        }
        lock (_lock)
        {
            // In binary mode (both sides agreed) a CR is just a byte.
            byte[] encoded = EncodeOutgoing(buffer, offset, count, _local[OptBinary] == OptionState.On);
            _inner.Write(encoded, 0, encoded.Length);
        }
    }

    public override void Flush()
    {
        lock (_lock)
        {
            _inner.Flush();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Turns the user's bytes into what is sent over the wire: IAC is doubled and a CR that is not followed by LF
    /// (or NUL) gets a NUL, because a bare CR is not a line end for the Network Virtual Terminal.
    /// </summary>
    internal static byte[] EncodeOutgoing(byte[] buffer, int offset, int count)
    {
        return EncodeOutgoing(buffer, offset, count, binary: false);
    }

    internal static byte[] EncodeOutgoing(byte[] buffer, int offset, int count, bool binary)
    {
        MemoryStream result = new MemoryStream(count + 8);
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            byte b = buffer[i];
            result.WriteByte(b);
            if (b == IAC)
            {
                result.WriteByte(IAC);
            }
            else if (b == CR && !binary)
            {
                bool followedByLineFeedOrNul = i + 1 < end && (buffer[i + 1] == LF || buffer[i + 1] == NUL);
                if (!followedByLineFeedOrNul)
                {
                    result.WriteByte(NUL);
                }
            }
        }
        return result.ToArray();
    }

    // Runs the received bytes through the state machine. Data goes to 'output', answers are queued in
    // _pendingReplies. Returns the number of data bytes produced. The caller holds _lock.
    private int Process(byte[] input, int length, byte[] output, int outputOffset)
    {
        int produced = 0;
        for (int i = 0; i < length; i++)
        {
            byte b = input[i];
            switch (_state)
            {
                case ParseState.Data:
                    if (b == IAC)
                    {
                        _state = ParseState.Iac;
                    }
                    else if (b == NUL && _previousWasCr && _remote[OptBinary] != OptionState.On)
                    {
                        // CR NUL is a bare carriage return: the NUL is only there to tell it from CR LF.
                        _previousWasCr = false;
                    }
                    else
                    {
                        output[outputOffset + produced++] = b;
                        _previousWasCr = b == CR;
                    }
                    break;

                case ParseState.Iac:
                    switch (b)
                    {
                        case IAC:
                            output[outputOffset + produced++] = IAC;
                            _previousWasCr = false;
                            _state = ParseState.Data;
                            break;
                        case WILL:
                            _state = ParseState.Will;
                            break;
                        case WONT:
                            _state = ParseState.Wont;
                            break;
                        case DO:
                            _state = ParseState.Do;
                            break;
                        case DONT:
                            _state = ParseState.Dont;
                            break;
                        case SB:
                            _state = ParseState.SubOption;
                            break;
                        default:
                            // NOP, Data Mark, Break, Go Ahead and the like need no answer here.
                            _state = ParseState.Data;
                            break;
                    }
                    break;

                case ParseState.Will:
                    OnWill(b);
                    _state = ParseState.Data;
                    break;

                case ParseState.Wont:
                    OnWont(b);
                    _state = ParseState.Data;
                    break;

                case ParseState.Do:
                    OnDo(b);
                    _state = ParseState.Data;
                    break;

                case ParseState.Dont:
                    OnDont(b);
                    _state = ParseState.Data;
                    break;

                case ParseState.SubOption:
                    _subOption = b;
                    _subLength = 0;
                    _state = ParseState.SubData;
                    break;

                case ParseState.SubData:
                    if (b == IAC)
                    {
                        _state = ParseState.SubIac;
                    }
                    else
                    {
                        AppendSubData(b);
                    }
                    break;

                case ParseState.SubIac:
                    if (b == SE)
                    {
                        OnSubnegotiation();
                        _state = ParseState.Data;
                    }
                    else if (b == IAC)
                    {
                        AppendSubData(IAC);
                        _state = ParseState.SubData;
                    }
                    else
                    {
                        // A command inside a subnegotiation ends it: handle the command as usual.
                        _state = ParseState.Iac;
                        i--;
                    }
                    break;
            }
        }
        return produced;
    }

    private void AppendSubData(byte b)
    {
        if (_subLength < _subData.Length)
        {
            _subData[_subLength++] = b;
        }
    }

    // An option is switched on or off once; a repeated request is not answered again, which keeps two
    // clients that both answer everything from talking to each other forever. The answer to our own offer
    // (state Requested) is taken in silently for the same reason.

    private static bool AcceptsFromServer(byte option)
    {
        return option == OptEcho || option == OptSuppressGoAhead || option == OptBinary;
    }

    private static bool AcceptsFromUs(byte option)
    {
        return option == OptSuppressGoAhead || option == OptTerminalType || option == OptNaws || option == OptBinary;
    }

    private void OnWill(byte option)
    {
        switch (_remote[option])
        {
            case OptionState.On:
                return;
            case OptionState.Requested:
                _remote[option] = OptionState.On;
                return;
        }
        if (AcceptsFromServer(option))
        {
            _remote[option] = OptionState.On;
            Reply(IAC, DO, option);
        }
        else
        {
            Reply(IAC, DONT, option);
        }
    }

    private void OnWont(byte option)
    {
        switch (_remote[option])
        {
            case OptionState.On:
                _remote[option] = OptionState.Off;
                Reply(IAC, DONT, option);
                break;
            case OptionState.Requested:
                _remote[option] = OptionState.Off;
                break;
        }
    }

    private void OnDo(byte option)
    {
        OptionState previous = _local[option];
        if (previous == OptionState.On)
        {
            return;
        }
        if (previous == OptionState.Requested || AcceptsFromUs(option))
        {
            _local[option] = OptionState.On;
            if (previous == OptionState.Off)
            {
                Reply(IAC, WILL, option);
            }
            if (option == OptNaws)
            {
                byte[] message = BuildWindowSizeMessage();
                _pendingReplies.Write(message, 0, message.Length);
            }
        }
        else
        {
            Reply(IAC, WONT, option);
        }
    }

    private void OnDont(byte option)
    {
        switch (_local[option])
        {
            case OptionState.On:
                _local[option] = OptionState.Off;
                Reply(IAC, WONT, option);
                break;
            case OptionState.Requested:
                _local[option] = OptionState.Off;
                break;
        }
    }

    private void OnSubnegotiation()
    {
        if (_subOption == OptTerminalType && _local[OptTerminalType] == OptionState.On && _subLength >= 1 && _subData[0] == TerminalTypeSend)
        {
            Reply(IAC, SB, OptTerminalType);
            _pendingReplies.WriteByte(TerminalTypeIs);
            byte[] name = Encoding.ASCII.GetBytes(_terminalType);
            _pendingReplies.Write(name, 0, name.Length);
            Reply(IAC, SE);
        }
        // Other subnegotiations are not used by the options we accepted.
    }

    private byte[] BuildWindowSizeMessage()
    {
        MemoryStream message = new MemoryStream(16);
        message.WriteByte(IAC);
        message.WriteByte(SB);
        message.WriteByte(OptNaws);
        WriteEscaped(message, (byte)(_columns >> 8));
        WriteEscaped(message, (byte)(_columns & 0xFF));
        WriteEscaped(message, (byte)(_rows >> 8));
        WriteEscaped(message, (byte)(_rows & 0xFF));
        message.WriteByte(IAC);
        message.WriteByte(SE);
        return message.ToArray();
    }

    private static void WriteEscaped(Stream stream, byte b)
    {
        stream.WriteByte(b);
        if (b == IAC)
        {
            stream.WriteByte(IAC);
        }
    }

    private void Reply(params byte[] bytes)
    {
        _pendingReplies.Write(bytes, 0, bytes.Length);
    }

    // Sends the queued answers. A write error is not reported here: the connection is broken then, and the next
    // read says so (the data that came with the commands must not be lost because of it).
    private void FlushReplies()
    {
        if (_pendingReplies.Length == 0)
        {
            return;
        }
        byte[] replies = _pendingReplies.ToArray();
        _pendingReplies.SetLength(0);
        try
        {
            _inner.Write(replies, 0, replies.Length);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static int ClampSize(int value, int fallback)
    {
        if (value <= 0)
        {
            return fallback;
        }
        return Math.Min(value, 65535);
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(offset < 0 ? nameof(offset) : nameof(count));
        }
    }
}

/// <summary>What a <see cref="TelnetStream"/> needs to know about the terminal and the way to negotiate.</summary>
internal sealed class TelnetOptions
{
    public string TerminalType { get; set; } = TelnetStream.DefaultTerminalType;

    public int Columns { get; set; } = TelnetStream.DefaultColumns;

    public int Rows { get; set; } = TelnetStream.DefaultRows;

    /// <summary>Offer the terminal type, the window size and SUPPRESS-GO-AHEAD right after connecting.</summary>
    public bool ActiveNegotiation { get; set; }
}
