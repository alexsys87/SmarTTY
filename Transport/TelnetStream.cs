using System;
using System.Collections.Generic;
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
///   NAWS (31)                          - reports the terminal size, again whenever it changes,
///   LINEMODE (34)                      - only when allowed (<see cref="TelnetOptions.AllowLineMode"/>): see below,
///   TSPEED (32)                        - the terminal speed (RFC 1079), only when one is configured,
///   XDISPLOC (35)                      - the X display location (RFC 1096), only when one is configured,
///   NEW-ENVIRON (39)                   - the environment variables (RFC 1572), only when some are configured,
///   STATUS (5)                         - the state of the options is reported on request (RFC 859), and the server's
///                                        state can be asked for (<see cref="RequestRemoteStatus"/>).
/// The three options that hand data to the server (speed, display, environment) are never accepted unless the
/// user configured something to hand over: a variable such as USER is acted upon by some servers.
///
/// LINEMODE (RFC 1184): when the server asks for it and the MODE it sets includes EDIT, what the user types is kept
/// in a line and sent as a whole when the line ends; Backspace / Delete, Ctrl+U and Ctrl+W edit the line, and the
/// escape sequences of the cursor keys are dropped. With TRAPSIG the interrupt, abort and suspend characters are
/// sent as the Telnet commands IP, ABORT and SUSP instead of as characters. The server's special characters
/// (SLC) are taken over for these functions, the FORWARDMASK is declined. Showing the typed line is the
/// terminal's business (local echo), the stream only sends it.
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
    internal const byte Eof = 236;
    internal const byte Susp = 237;
    internal const byte Abort = 238;
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
    internal const byte OptLineMode = 34;
    internal const byte OptStatus = 5;
    internal const byte OptTSpeed = 32;
    internal const byte OptXDispLoc = 35;
    internal const byte OptNewEnviron = 39;

    // TERMINAL-TYPE subnegotiation (RFC 1091).
    private const byte TerminalTypeIs = 0;
    private const byte TerminalTypeSend = 1;

    // TSPEED (RFC 1079), XDISPLOC (RFC 1096) and STATUS (RFC 859) use the same two subnegotiation commands.
    private const byte SubIs = 0;
    private const byte SubSend = 1;

    // NEW-ENVIRON subnegotiation (RFC 1572).
    private const byte EnvIs = 0;
    private const byte EnvSend = 1;
    private const byte EnvVar = 0;
    private const byte EnvValue = 1;
    private const byte EnvEsc = 2;
    private const byte EnvUserVar = 3;

    // The variables RFC 1408 / 1572 call well known are sent as VAR, all the others as USERVAR.
    private static readonly string[] WellKnownVariables = { "USER", "JOB", "ACCT", "PRINTER", "SYSTEMTYPE", "DISPLAY" };

    // LINEMODE subnegotiation (RFC 1184).
    private const byte LmMode = 1;
    private const byte LmForwardMask = 2;
    private const byte LmSlc = 3;
    private const byte ModeEdit = 1;
    private const byte ModeTrapSig = 2;
    private const byte ModeAck = 4;
    private const byte ModeKnownBits = 0x1F;   // EDIT, TRAPSIG, MODE_ACK, SOFT_TAB, LIT_ECHO

    // Special characters (SLC): the functions we do something with, the level bits of the flags and the ACK bit.
    private const byte SlcIp = 3;
    private const byte SlcAbort = 7;
    private const byte SlcSusp = 9;
    private const byte SlcEc = 10;
    private const byte SlcEl = 11;
    private const byte SlcEw = 12;
    private const byte SlcLevelMask = 3;
    private const byte SlcNoSupport = 0;
    private const byte SlcVariable = 2;
    private const byte SlcDefault = 3;
    private const byte SlcAck = 0x80;

    // A line typed in line mode that is longer than this is sent without waiting for its end.
    private const int MaxLineLength = 8192;

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

    private readonly TelnetAutoLogin _autoLogin;

    private readonly bool _allowLineMode;

    // What is handed to the server on request; null / empty: the option is refused.
    private readonly string _terminalSpeed;

    private readonly string _xDisplayLocation;

    private readonly List<KeyValuePair<string, string>> _environment;

    // The server's answer to a STATUS request, and whether it arrived since the reading thread last looked.
    private string _remoteStatus;

    private bool _remoteStatusArrived;

    // Guards everything below and every write to _inner.
    private readonly object _lock = new object();

    private readonly byte[] _readBuffer = new byte[4096];

    private readonly MemoryStream _pendingReplies = new MemoryStream();

    // The server's side of an option (WILL from it, DO from us) and our side of it (WILL from us, DO from it).
    private readonly OptionState[] _remote = new OptionState[256];

    private readonly OptionState[] _local = new OptionState[256];

    private bool _echoReported;

    // LINEMODE state: the MODE the server asked for (all the bits it set) and whether one was received at all,
    // the line typed so far, and the special characters (-1: the function is not used).
    private byte _lineModeMask;

    private bool _lineModeSeen;

    private readonly MemoryStream _line = new MemoryStream();

    private bool _skipLineFeed;

    private int _escapeState;

    private int _slcIp = 3;

    private int _slcAbort = 28;

    private int _slcSusp = 26;

    private int _slcEc = 127;

    private int _slcEl = 21;

    private int _slcEw = 23;

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
        _autoLogin = options.AutoLogin;
        _allowLineMode = options.AllowLineMode;
        _terminalSpeed = NormalizeTerminalSpeed(options.TerminalSpeed);
        _xDisplayLocation = NormalizeDisplayLocation(options.XDisplayLocation);
        _environment = BuildEnvironment(options.Environment, _xDisplayLocation);
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
            MemoryStream offer = new MemoryStream();
            byte[] basic =
            {
                IAC, DO, OptSuppressGoAhead,
                IAC, WILL, OptSuppressGoAhead,
                IAC, WILL, OptTerminalType,
                IAC, WILL, OptNaws
            };
            offer.Write(basic, 0, basic.Length);
            // What the user configured to hand over is offered as well; the rest stays unmentioned.
            OfferLocal(offer, OptTSpeed, _terminalSpeed != null);
            OfferLocal(offer, OptXDispLoc, _xDisplayLocation != null);
            OfferLocal(offer, OptNewEnviron, _environment.Count > 0);
            byte[] bytes = offer.ToArray();
            _inner.Write(bytes, 0, bytes.Length);
        }
    }

    private void OfferLocal(MemoryStream offer, byte option, bool wanted)
    {
        if (!wanted)
        {
            return;
        }
        _local[option] = OptionState.Requested;
        offer.WriteByte(IAC);
        offer.WriteByte(WILL);
        offer.WriteByte(option);
    }

    /// <summary>Raised on the reading thread when the server's answer to <see cref="RequestRemoteStatus"/> arrived.</summary>
    public event EventHandler RemoteStatusReceived;

    /// <summary>
    /// What the server reported about its options (option STATUS), one entry per line; null until it answered.
    /// </summary>
    public string RemoteStatus
    {
        get
        {
            lock (_lock)
            {
                return _remoteStatus;
            }
        }
    }

    /// <summary>
    /// Asks the server to report the state of its options (STATUS SEND). False when the server did not offer STATUS
    /// (it has not sent WILL STATUS); the answer arrives later, see <see cref="RemoteStatusReceived"/>.
    /// </summary>
    public bool RequestRemoteStatus()
    {
        lock (_lock)
        {
            if (_remote[OptStatus] != OptionState.On)
            {
                return false;
            }
            _inner.Write(new[] { IAC, SB, OptStatus, SubSend, IAC, SE }, 0, 6);
            return true;
        }
    }

    /// <summary>The options in force on our side and on the server's side, as text for the user.</summary>
    public string DescribeOptions()
    {
        lock (_lock)
        {
            return "We do:\r\n" + DescribeOptions(_local) + "\r\n\r\nThe server does:\r\n" + DescribeOptions(_remote);
        }
    }

    private static string DescribeOptions(OptionState[] states)
    {
        List<string> names = new List<string>();
        for (int option = 0; option < states.Length; option++)
        {
            if (states[option] == OptionState.On)
            {
                names.Add("  " + OptionName((byte)option));
            }
        }
        return names.Count == 0 ? "  (nothing)" : string.Join("\r\n", names.ToArray());
    }

    internal static string OptionName(byte option)
    {
        switch (option)
        {
            case OptBinary: return "BINARY";
            case OptEcho: return "ECHO";
            case OptStatus: return "STATUS";
            case OptSuppressGoAhead: return "SUPPRESS-GO-AHEAD";
            case OptTerminalType: return "TERMINAL-TYPE";
            case OptNaws: return "NAWS";
            case OptTSpeed: return "TSPEED";
            case 33: return "TOGGLE-FLOW-CONTROL";
            case OptLineMode: return "LINEMODE";
            case OptXDispLoc: return "XDISPLOC";
            case 37: return "AUTHENTICATION";
            case 38: return "ENCRYPT";
            case OptNewEnviron: return "NEW-ENVIRON";
            case 42: return "CHARSET";
            case 44: return "COM-PORT";
            case 46: return "START-TLS";
            default: return "option " + option;
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

    /// <summary>True while the server has put us in line mode with line editing (LINEMODE with EDIT).</summary>
    public bool LineEditing
    {
        get
        {
            lock (_lock)
            {
                return IsLineEditing;
            }
        }
    }

    private bool IsLineEditing => _local[OptLineMode] == OptionState.On && (_lineModeMask & ModeEdit) != 0;

    private bool IsTrappingSignals => _local[OptLineMode] == OptionState.On && (_lineModeMask & ModeTrapSig) != 0;

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
            bool statusArrived;
            lock (_lock)
            {
                produced = Process(_readBuffer, received, buffer, offset);
                FlushReplies();
                bool echoNow = _remote[OptEcho] == OptionState.On;
                echoChanged = echoNow != _echoReported;
                _echoReported = echoNow;
                statusArrived = _remoteStatusArrived;
                _remoteStatusArrived = false;
            }
            if (statusArrived)
            {
                RemoteStatusReceived?.Invoke(this, EventArgs.Empty);
            }
            if (echoChanged)
            {
                // Outside the lock: the handler may call back into the stream.
                RemoteEchoChanged?.Invoke(this, EventArgs.Empty);
            }
            if (produced > 0)
            {
                if (_autoLogin != null && !_autoLogin.IsFinished)
                {
                    AnswerPrompts(buffer, offset, produced);
                }
                return produced;
            }
            // The chunk held only commands: wait for the text that follows.
        }
    }

    // Types the user name / password when the text just received ended with the prompt for it. Runs on the reading
    // thread, outside the lock, and goes through Write() like the user's own typing.
    private void AnswerPrompts(byte[] buffer, int offset, int count)
    {
        try
        {
            foreach (string line in _autoLogin.Feed(buffer, offset, count))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                Write(bytes, 0, bytes.Length);
            }
        }
        catch (IOException)
        {
            // The connection is broken; the next read says so.
        }
        catch (ObjectDisposedException)
        {
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
            bool binary = _local[OptBinary] == OptionState.On;
            byte[] encoded = IsLineEditing || IsTrappingSignals
                ? EncodeLineModeInput(buffer, offset, count, binary)
                : EncodeOutgoing(buffer, offset, count, binary);
            if (encoded.Length > 0)
            {
                _inner.Write(encoded, 0, encoded.Length);
            }
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
        return option == OptEcho || option == OptSuppressGoAhead || option == OptBinary || option == OptStatus;
    }

    private bool AcceptsFromUs(byte option)
    {
        return option == OptSuppressGoAhead || option == OptTerminalType || option == OptNaws || option == OptBinary
            || option == OptStatus
            || (option == OptLineMode && _allowLineMode)
            || (option == OptTSpeed && _terminalSpeed != null)
            || (option == OptXDispLoc && _xDisplayLocation != null)
            || (option == OptNewEnviron && _environment.Count > 0);
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
                if (option == OptLineMode)
                {
                    LeaveLineMode();
                }
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
        else if (_subOption == OptTSpeed && _local[OptTSpeed] == OptionState.On && _subLength >= 1 && _subData[0] == SubSend)
        {
            ReplyText(OptTSpeed, _terminalSpeed);
        }
        else if (_subOption == OptXDispLoc && _local[OptXDispLoc] == OptionState.On && _subLength >= 1 && _subData[0] == SubSend)
        {
            ReplyText(OptXDispLoc, _xDisplayLocation);
        }
        else if (_subOption == OptNewEnviron && _local[OptNewEnviron] == OptionState.On && _subLength >= 1 && _subData[0] == EnvSend)
        {
            OnEnvironmentRequest();
        }
        else if (_subOption == OptStatus && _local[OptStatus] == OptionState.On && _subLength >= 1 && _subData[0] == SubSend)
        {
            ReplyStatus();
        }
        else if (_subOption == OptStatus && _remote[OptStatus] == OptionState.On && _subLength >= 1 && _subData[0] == SubIs)
        {
            _remoteStatus = ParseStatus(_subData, 1, _subLength - 1);
            _remoteStatusArrived = true;
        }
        else if (_subOption == OptLineMode && _local[OptLineMode] == OptionState.On && _subLength >= 1)
        {
            switch (_subData[0])
            {
                case LmMode:
                    OnLineModeMode();
                    break;
                case LmSlc:
                    OnSpecialCharacters();
                    break;
                case DO:
                    // The forward mask (characters that send the line at once) is not supported: the line is only
                    // sent when it ends, which the server has to expect from a client that declines.
                    if (_subLength >= 2 && _subData[1] == LmForwardMask)
                    {
                        Reply(IAC, SB, OptLineMode, WONT, LmForwardMask, IAC, SE);
                    }
                    break;
            }
        }
        // Other subnegotiations are not used by the options we accepted.
    }

    // MODE: the server tells how the line is to be handled. A mode we did not have is acknowledged by sending it
    // back with the ACK bit; the ACK of a mode we sent ourselves (we never do) and a repeated mode need no answer.
    private void OnLineModeMode()
    {
        if (_subLength < 2)
        {
            return;
        }
        byte mask = (byte)(_subData[1] & ModeKnownBits);
        if ((mask & ModeAck) != 0)
        {
            return;
        }
        bool changed = !_lineModeSeen || mask != _lineModeMask;
        bool wasEditing = IsLineEditing;
        _lineModeMask = mask;
        _lineModeSeen = true;
        if (changed)
        {
            Reply(IAC, SB, OptLineMode, LmMode, (byte)(mask | ModeAck), IAC, SE);
        }
        if (wasEditing && !IsLineEditing)
        {
            // Back to characters: what was typed so far goes out now.
            FlushLine(_pendingReplies, newLine: false);
        }
    }

    // SLC: the server's special characters. The ones we act on are taken over, the rest is declined, and a request
    // for our defaults (function 0 at level DEFAULT) is answered with them.
    private void OnSpecialCharacters()
    {
        MemoryStream answer = new MemoryStream();
        for (int i = 1; i + 2 < _subLength; i += 3)
        {
            byte function = _subData[i];
            byte flags = _subData[i + 1];
            byte value = _subData[i + 2];
            int level = flags & SlcLevelMask;
            if ((flags & SlcAck) != 0)
            {
                continue;
            }
            if (function == 0 && level == SlcDefault)
            {
                AddSlc(answer, SlcIp, SlcVariable, 3);
                AddSlc(answer, SlcAbort, SlcVariable, 28);
                AddSlc(answer, SlcSusp, SlcVariable, 26);
                AddSlc(answer, SlcEc, SlcVariable, 127);
                AddSlc(answer, SlcEl, SlcVariable, 21);
                AddSlc(answer, SlcEw, SlcVariable, 23);
                _slcIp = 3;
                _slcAbort = 28;
                _slcSusp = 26;
                _slcEc = 127;
                _slcEl = 21;
                _slcEw = 23;
                continue;
            }
            if (function != SlcIp && function != SlcAbort && function != SlcSusp && function != SlcEc && function != SlcEl && function != SlcEw)
            {
                if (level != SlcNoSupport)
                {
                    AddSlc(answer, function, SlcNoSupport, 0);
                }
                continue;
            }
            int character = level == SlcNoSupport || value == 0 || value == IAC ? -1 : value;
            SetSpecialCharacter(function, character);
            AddSlc(answer, function, (byte)(flags | SlcAck), value);
        }
        if (answer.Length > 0)
        {
            Reply(IAC, SB, OptLineMode, LmSlc);
            byte[] triplets = answer.ToArray();
            _pendingReplies.Write(triplets, 0, triplets.Length);
            Reply(IAC, SE);
        }
    }

    private void SetSpecialCharacter(byte function, int character)
    {
        switch (function)
        {
            case SlcIp:
                _slcIp = character;
                break;
            case SlcAbort:
                _slcAbort = character;
                break;
            case SlcSusp:
                _slcSusp = character;
                break;
            case SlcEc:
                _slcEc = character;
                break;
            case SlcEl:
                _slcEl = character;
                break;
            case SlcEw:
                _slcEw = character;
                break;
        }
    }

    private static void AddSlc(MemoryStream answer, byte function, byte flags, byte value)
    {
        WriteEscaped(answer, function);
        WriteEscaped(answer, flags);
        WriteEscaped(answer, value);
    }

    // The option was switched off (the caller has already changed its state): what was typed goes out as it is.
    private void LeaveLineMode()
    {
        _lineModeMask = 0;
        _lineModeSeen = false;
        _escapeState = 0;
        if (_line.Length > 0)
        {
            FlushLine(_pendingReplies, newLine: false);
        }
    }

    // Moves the typed line into 'output' (with the line end when it ended), ready to be sent.
    private void FlushLine(Stream output, bool newLine)
    {
        byte[] text = _line.ToArray();
        _line.SetLength(0);
        if (text.Length > 0)
        {
            byte[] encoded = EncodeOutgoing(text, 0, text.Length, binary: false);
            output.Write(encoded, 0, encoded.Length);
        }
        if (newLine)
        {
            output.WriteByte(CR);
            output.WriteByte(LF);
        }
    }

    // What the user typed while the server wants line mode: the line is collected and edited here and goes out when
    // it ends. The caller holds _lock.
    private byte[] EncodeLineModeInput(byte[] buffer, int offset, int count, bool binary)
    {
        bool edit = IsLineEditing;
        bool trap = IsTrappingSignals;
        MemoryStream output = new MemoryStream(count + 8);
        MemoryStream passThrough = new MemoryStream();
        for (int i = offset; i < offset + count; i++)
        {
            byte b = buffer[i];
            if (edit && _escapeState != 0)
            {
                // The escape sequence of a cursor key means nothing in a line that is edited here: it is dropped.
                if (_escapeState == 1)
                {
                    if (b == '[')
                    {
                        _escapeState = 2;
                        continue;
                    }
                    if (b == 'O')
                    {
                        _escapeState = 3;
                        continue;
                    }
                    // A lone Escape: this byte is an ordinary one.
                    _escapeState = 0;
                }
                else if (_escapeState == 2)
                {
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        _escapeState = 0;
                    }
                    continue;
                }
                else
                {
                    _escapeState = 0;
                    continue;
                }
            }
            if (trap && (b == _slcIp || b == _slcAbort || b == _slcSusp))
            {
                FlushPassThrough(passThrough, output, binary);
                _line.SetLength(0);
                output.WriteByte(IAC);
                output.WriteByte(b == _slcIp ? InterruptProcess : b == _slcAbort ? Abort : Susp);
                continue;
            }
            if (!edit)
            {
                passThrough.WriteByte(b);
                continue;
            }
            if (b == 0x1B)
            {
                _escapeState = 1;
                continue;
            }
            if (b == CR)
            {
                FlushLine(output, newLine: true);
                _skipLineFeed = true;
                continue;
            }
            if (b == LF)
            {
                if (_skipLineFeed)
                {
                    _skipLineFeed = false;
                }
                else
                {
                    FlushLine(output, newLine: true);
                }
                continue;
            }
            _skipLineFeed = false;
            if (b == 8 || b == 127 || b == _slcEc)
            {
                EraseLastCharacter();
            }
            else if (b == _slcEl)
            {
                _line.SetLength(0);
            }
            else if (b == _slcEw)
            {
                EraseLastWord();
            }
            else
            {
                _line.WriteByte(b);
                if (_line.Length >= MaxLineLength)
                {
                    FlushLine(output, newLine: false);
                }
            }
        }
        FlushPassThrough(passThrough, output, binary);
        return output.ToArray();
    }

    private static void FlushPassThrough(MemoryStream passThrough, Stream output, bool binary)
    {
        if (passThrough.Length == 0)
        {
            return;
        }
        byte[] raw = passThrough.ToArray();
        passThrough.SetLength(0);
        byte[] encoded = EncodeOutgoing(raw, 0, raw.Length, binary);
        output.Write(encoded, 0, encoded.Length);
    }

    // Removes the last character of the line; a character of the UTF-8 text is a lead byte and its continuation bytes.
    private void EraseLastCharacter()
    {
        byte[] data = _line.GetBuffer();
        int length = (int)_line.Length;
        if (length == 0)
        {
            return;
        }
        length--;
        while (length > 0 && (data[length] & 0xC0) == 0x80)
        {
            length--;
        }
        _line.SetLength(length);
    }

    private void EraseLastWord()
    {
        byte[] data = _line.GetBuffer();
        int length = (int)_line.Length;
        while (length > 0 && data[length - 1] == (byte)' ')
        {
            length--;
        }
        while (length > 0 && data[length - 1] != (byte)' ')
        {
            length--;
        }
        _line.SetLength(length);
    }

    // IAC SB <option> IS <text> IAC SE (TSPEED, XDISPLOC): the text is plain ASCII without any byte that needs escaping.
    private void ReplyText(byte option, string text)
    {
        Reply(IAC, SB, option, SubIs);
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        _pendingReplies.Write(bytes, 0, bytes.Length);
        Reply(IAC, SE);
    }

    // The SEND of NEW-ENVIRON lists the variables the server wants: VAR / USERVAR followed by a name, a VAR or USERVAR
    // without a name stands for all the variables of that kind, and an empty list for all of them. Every variable we
    // have is answered with its value; one we do not have is answered with its name alone (it is not defined).
    private void OnEnvironmentRequest()
    {
        List<KeyValuePair<byte, string>> requests = new List<KeyValuePair<byte, string>>();
        int i = 1;
        while (i < _subLength)
        {
            byte type = _subData[i++];
            if (type != EnvVar && type != EnvUserVar)
            {
                continue;
            }
            MemoryStream name = new MemoryStream();
            while (i < _subLength && _subData[i] != EnvVar && _subData[i] != EnvUserVar)
            {
                if (_subData[i] == EnvEsc && i + 1 < _subLength)
                {
                    i++;
                }
                name.WriteByte(_subData[i++]);
            }
            requests.Add(new KeyValuePair<byte, string>(type, name.Length == 0 ? null : Encoding.UTF8.GetString(name.ToArray())));
        }
        MemoryStream answer = new MemoryStream();
        HashSet<string> added = new HashSet<string>(StringComparer.Ordinal);
        if (requests.Count == 0)
        {
            foreach (KeyValuePair<string, string> variable in _environment)
            {
                AddVariable(answer, added, IsWellKnownVariable(variable.Key) ? EnvVar : EnvUserVar, variable.Key, variable.Value);
            }
        }
        foreach (KeyValuePair<byte, string> request in requests)
        {
            if (request.Value == null)
            {
                foreach (KeyValuePair<string, string> variable in _environment)
                {
                    bool wellKnown = IsWellKnownVariable(variable.Key);
                    if (wellKnown == (request.Key == EnvVar))
                    {
                        AddVariable(answer, added, request.Key, variable.Key, variable.Value);
                    }
                }
                continue;
            }
            string value = null;
            foreach (KeyValuePair<string, string> variable in _environment)
            {
                if (string.Equals(variable.Key, request.Value, StringComparison.Ordinal))
                {
                    value = variable.Value;
                }
            }
            AddVariable(answer, added, request.Key, request.Value, value);
        }
        Reply(IAC, SB, OptNewEnviron, EnvIs);
        byte[] bytes = answer.ToArray();
        _pendingReplies.Write(bytes, 0, bytes.Length);
        Reply(IAC, SE);
    }

    private static bool IsWellKnownVariable(string name)
    {
        return Array.IndexOf(WellKnownVariables, name) >= 0;
    }

    // value == null: the variable is not defined (its name only is sent).
    private static void AddVariable(MemoryStream answer, HashSet<string> added, byte type, string name, string value)
    {
        if (!added.Add(type + ":" + name))
        {
            return;
        }
        answer.WriteByte(type);
        WriteEnvironmentText(answer, name);
        if (value != null)
        {
            answer.WriteByte(EnvValue);
            WriteEnvironmentText(answer, value);
        }
    }

    // A name or value may contain the bytes that mean something in the list: they are preceded by ESC (and IAC doubled).
    private static void WriteEnvironmentText(MemoryStream answer, string text)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            if (b == EnvVar || b == EnvValue || b == EnvEsc || b == EnvUserVar)
            {
                answer.WriteByte(EnvEsc);
            }
            WriteEscaped(answer, b);
        }
    }

    // STATUS IS: our side of every option that is on (WILL) and the server's side (DO). In the list, IAC and SE
    // (a value of an option code) are doubled.
    private void ReplyStatus()
    {
        MemoryStream status = new MemoryStream();
        for (int option = 0; option < 256; option++)
        {
            if (_local[option] == OptionState.On)
            {
                status.WriteByte(WILL);
                WriteStatusByte(status, (byte)option);
            }
            if (_remote[option] == OptionState.On)
            {
                status.WriteByte(DO);
                WriteStatusByte(status, (byte)option);
            }
        }
        Reply(IAC, SB, OptStatus, SubIs);
        byte[] bytes = status.ToArray();
        _pendingReplies.Write(bytes, 0, bytes.Length);
        Reply(IAC, SE);
    }

    private static void WriteStatusByte(MemoryStream status, byte b)
    {
        status.WriteByte(b);
        if (b == IAC || b == SE)
        {
            status.WriteByte(b);
        }
    }

    // Reads the server's STATUS IS: WILL / WONT / DO / DONT <option> entries, and SB ... SE for an option with
    // parameters (shown with its size only). The entries are returned one per line.
    private static string ParseStatus(byte[] data, int start, int length)
    {
        List<string> entries = new List<string>();
        int end = start + length;
        int i = start;
        while (i < end)
        {
            byte verb = data[i++];
            if (verb == WILL || verb == WONT || verb == DO || verb == DONT)
            {
                if (i >= end)
                {
                    break;
                }
                byte option = data[i++];
                if (option == SE && i < end && data[i] == SE)
                {
                    i++;
                }
                string name = verb == WILL ? "WILL " : verb == WONT ? "WONT " : verb == DO ? "DO " : "DONT ";
                entries.Add(name + OptionName(option));
            }
            else if (verb == SB && i < end)
            {
                byte option = data[i++];
                int size = 0;
                while (i < end)
                {
                    byte b = data[i++];
                    if (b == SE)
                    {
                        if (i < end && data[i] == SE)
                        {
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    size++;
                }
                entries.Add("SB " + OptionName(option) + " (" + size + " bytes of parameters)");
            }
        }
        return entries.Count == 0 ? "(no options)" : string.Join("\r\n", entries.ToArray());
    }

    /// <summary>
    /// "9600" or "38400,9600" (transmit,receive) as sent in TSPEED; "9600" alone stands for both directions.
    /// Null for an empty or invalid text: the option is then refused.
    /// </summary>
    internal static string NormalizeTerminalSpeed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string[] parts = text.Trim().Split(',');
        if (parts.Length == 1)
        {
            parts = new[] { parts[0], parts[0] };
        }
        if (parts.Length != 2)
        {
            return null;
        }
        string transmit = parts[0].Trim();
        string receive = parts[1].Trim();
        return IsSpeed(transmit) && IsSpeed(receive) ? transmit + "," + receive : null;
    }

    private static bool IsSpeed(string text)
    {
        if (text.Length < 1 || text.Length > 9)
        {
            return false;
        }
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }
        return text.TrimStart('0').Length > 0;
    }

    /// <summary>The X display location (host:display[.screen]): printable ASCII only; null when empty or invalid.</summary>
    internal static string NormalizeDisplayLocation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        text = text.Trim();
        if (text.Length > 255)
        {
            return null;
        }
        foreach (char c in text)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return null;
            }
        }
        return text;
    }

    // The variables to hand over: valid ones only, later entries replace earlier ones with the same name, and
    // DISPLAY comes from the X display location unless it was set explicitly.
    private static List<KeyValuePair<string, string>> BuildEnvironment(IEnumerable<KeyValuePair<string, string>> variables, string displayLocation)
    {
        List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
        if (variables != null)
        {
            foreach (KeyValuePair<string, string> variable in variables)
            {
                if (result.Count >= TelnetEnvironment.MaxVariables || !TelnetEnvironment.IsValidName(variable.Key) || !TelnetEnvironment.IsValidValue(variable.Value))
                {
                    continue;
                }
                int existing = result.FindIndex(candidate => string.Equals(candidate.Key, variable.Key, StringComparison.Ordinal));
                if (existing >= 0)
                {
                    result[existing] = variable;
                }
                else
                {
                    result.Add(variable);
                }
            }
        }
        if (displayLocation != null && !result.Exists(candidate => candidate.Key == "DISPLAY"))
        {
            result.Add(new KeyValuePair<string, string>("DISPLAY", displayLocation));
        }
        return result;
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

    /// <summary>
    /// Agree to LINEMODE when the server asks for it (by default the server is refused and keeps sending
    /// characters one by one).
    /// </summary>
    public bool AllowLineMode { get; set; }

    /// <summary>Types the user name and the password when the server asks for them; null for no automatic login.</summary>
    public TelnetAutoLogin AutoLogin { get; set; }

    /// <summary>
    /// Reported to the server on request (option TSPEED): "9600" or "38400,9600". Empty: the option is refused.
    /// </summary>
    public string TerminalSpeed { get; set; }

    /// <summary>
    /// Reported to the server on request (option XDISPLOC): host:display[.screen]. Empty: the option is refused.
    /// It also becomes the variable DISPLAY of NEW-ENVIRON unless that one is set explicitly.
    /// </summary>
    public string XDisplayLocation { get; set; }

    /// <summary>
    /// The variables handed to the server on request (option NEW-ENVIRON, see <see cref="TelnetEnvironment.Parse"/>).
    /// Empty (and no X display location): the option is refused.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> Environment { get; set; }
}

/// <summary>The variables of the NEW-ENVIRON option as the user writes them: one NAME=value per line.</summary>
internal static class TelnetEnvironment
{
    public const int MaxVariables = 64;

    public const int MaxNameLength = 64;

    public const int MaxValueLength = 256;

    /// <summary>
    /// Reads "NAME=value" lines. Blank lines and lines starting with '#' are skipped, and so is every line that is
    /// not valid (no '=', an empty or too long name, a control character); the first '=' ends the name.
    /// </summary>
    public static List<KeyValuePair<string, string>> Parse(string text)
    {
        List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim('\r').TrimStart();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            string name = line.Substring(0, equals).Trim();
            string value = line.Substring(equals + 1).Trim();
            if (IsValidName(name) && IsValidValue(value))
            {
                result.Add(new KeyValuePair<string, string>(name, value));
            }
        }
        return result;
    }

    public static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return false;
        }
        foreach (char c in name)
        {
            if (c <= 0x20 || c > 0x7E || c == '=')
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsValidValue(string value)
    {
        if (value == null || value.Length > MaxValueLength)
        {
            return false;
        }
        foreach (char c in value)
        {
            if (c < 0x20 || c == 0x7F)
            {
                return false;
            }
        }
        return true;
    }
}
