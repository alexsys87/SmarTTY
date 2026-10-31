using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
///   NEW-ENVIRON (39)                   - the environment variables (RFC 1572), when some are configured or when
///                                        <see cref="TelnetOptions.AcceptEnvironmentRequests"/> is set,
///   STATUS (5)                         - the state of the options, with the parameters of the subnegotiated ones, is
///                                        reported on request (RFC 859), and the server's state can be asked for
///                                        (<see cref="RequestRemoteStatus"/>),
///   TIMING-MARK (6)                    - answered with WILL each time the server asks (RFC 860),
///   CHARSET (42)                       - the server's REQUEST is accepted when it lists the terminal's character
///                                        set (<see cref="TelnetOptions.Charset"/>), rejected otherwise (RFC 2066),
///   START-TLS (46)                     - TLS inside the Telnet session, see below.
/// The options that hand data to the server (speed, display, environment) are never accepted unless the caller has
/// something to hand over: a variable such as USER is acted upon by some servers.
///
/// LINEMODE (RFC 1184): when the server asks for it and the MODE it sets includes EDIT, what the user types is kept
/// in a line and sent as a whole when the line ends; Backspace / Delete, Ctrl+U and Ctrl+W edit the line, and the
/// escape sequences of the cursor keys are dropped. Ctrl+D sends what was typed without a line end, or the Telnet
/// command EOF on an empty line, as a terminal driver does. With TRAPSIG the interrupt, abort and suspend characters
/// are sent as the Telnet commands IP, ABORT and SUSP instead of as characters; without it they are sent as
/// characters right away (with the text typed before them) instead of waiting in the line for its end. The server's
/// special characters (SLC) are taken over for these functions, the FORWARDMASK is declined. Showing the typed line
/// is the terminal's business (local echo), the stream only sends it.
///
/// START-TLS (draft-altman-telnet-starttls): with <see cref="TelnetOptions.StartTls"/> the server's DO START_TLS is
/// agreed to and both sides announce with "SB START_TLS FOLLOWS" that the TLS handshake comes next. Nothing else is
/// sent from then on: the typed data is held back. The handshake itself is made by <see cref="TelnetOptions.UpgradeToTls"/>
/// on the reading thread (the bytes that followed the server's FOLLOWS are handed to it); then the option state starts
/// from scratch inside the encrypted stream, as the draft wants, and the held data goes out. When STARTTLS is required
/// the client offers it itself (WILL START_TLS), nothing typed is sent before the handshake, and a refusal of the server
/// or any text it sends before the handshake ends the connection: a server that is impersonated or a connection that
/// is tampered with must not see a password in clear text.
///
/// By default we only answer: no option is offered on our own initiative, so a server that does not speak Telnet is
/// not confronted with unexpected commands. With <see cref="TelnetOptions.ActiveNegotiation"/> the terminal type,
/// the window size and SUPPRESS-GO-AHEAD are offered right after <see cref="Start"/>, as most telnet clients do.
///
/// One thread may read while others write. The protocol state is guarded by one lock that is never held while the
/// connection is written to; the bytes go out through a queue in the order they were produced, so a negotiation reply
/// never lands in the middle of the user's data. The reading thread never waits for the connection to take its
/// replies: when another thread is busy writing (a large paste the server does not take fast enough), the replies are
/// sent by that thread after its data, and the reading goes on. (Waiting there would be a deadlock: the server stops
/// reading when nobody reads what it sends.)
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
    internal const byte OptStatus = 5;
    internal const byte OptTimingMark = 6;
    internal const byte OptTerminalType = 24;
    internal const byte OptNaws = 31;
    internal const byte OptTSpeed = 32;
    internal const byte OptLineMode = 34;
    internal const byte OptXDispLoc = 35;
    internal const byte OptNewEnviron = 39;
    internal const byte OptCharset = 42;
    internal const byte OptStartTls = 46;

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

    // CHARSET subnegotiation (RFC 2066).
    private const byte CharsetRequest = 1;
    private const byte CharsetAccepted = 2;
    private const byte CharsetRejected = 3;
    private const byte CharsetTTableIs = 4;
    private const byte CharsetTTableRejected = 5;

    // START_TLS subnegotiation: "the TLS handshake follows".
    internal const byte StartTlsFollows = 1;

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
    private const byte SlcEof = 8;
    private const byte SlcSusp = 9;
    private const byte SlcEc = 10;
    private const byte SlcEl = 11;
    private const byte SlcEw = 12;
    private const byte SlcLevelMask = 3;
    private const byte SlcNoSupport = 0;
    private const byte SlcVariable = 2;
    private const byte SlcDefault = 3;
    private const byte SlcAck = 0x80;

    // Our default special characters: Ctrl+C, Ctrl+\, Ctrl+D, Ctrl+Z, DEL, Ctrl+U, Ctrl+W.
    private const int DefaultSlcIp = 3;
    private const int DefaultSlcAbort = 28;
    private const int DefaultSlcEof = 4;
    private const int DefaultSlcSusp = 26;
    private const int DefaultSlcEc = 127;
    private const int DefaultSlcEl = 21;
    private const int DefaultSlcEw = 23;

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
    private const int MaxSubnegotiationLength = 512;

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

    // Where STARTTLS stands: not (yet) agreed, our FOLLOWS sent, the server's FOLLOWS received (the reading thread
    // makes the handshake before it reads again), the session runs inside TLS.
    private enum TlsPhase
    {
        None,
        FollowsSent,
        HandshakeDue,
        Done
    }

    // Replaced once, by the encrypted stream of STARTTLS; only the reading thread replaces it (under _sendLock).
    private volatile Stream _inner;

    private readonly string _terminalType;

    private readonly bool _activeNegotiation;

    private readonly TelnetAutoLogin _autoLogin;

    private readonly bool _allowLineMode;

    // What is handed to the server on request; null / empty: the option is refused.
    private readonly string _terminalSpeed;

    private readonly string _xDisplayLocation;

    private readonly List<KeyValuePair<string, string>> _environment;

    private readonly bool _acceptEnvironmentRequests;

    private readonly string _charset;

    private readonly TelnetStartTlsMode _startTlsMode;

    private readonly Func<Stream, Stream> _upgradeToTls;

    // The server's answer to a STATUS request, and whether it arrived since the reading thread last looked.
    private string _remoteStatus;

    private bool _remoteStatusArrived;

    // Guards the protocol state (everything below). Never held while the connection is written to.
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

    private int _slcIp = DefaultSlcIp;

    private int _slcAbort = DefaultSlcAbort;

    private int _slcEof = DefaultSlcEof;

    private int _slcSusp = DefaultSlcSusp;

    private int _slcEc = DefaultSlcEc;

    private int _slcEl = DefaultSlcEl;

    private int _slcEw = DefaultSlcEw;

    private readonly byte[] _subData = new byte[MaxSubnegotiationLength];

    private ParseState _state = ParseState.Data;

    private byte _subOption;

    private int _subLength;

    private bool _previousWasCr;

    private int _columns;

    private int _rows;

    // CHARSET: the character set the server's REQUEST was answered with; null while none was agreed on.
    private string _acceptedCharset;

    // STARTTLS: the phase, the data typed while it has to wait for the handshake, the bytes that followed the
    // server's FOLLOWS (the beginning of the TLS conversation) and why the connection has to be given up.
    private volatile TlsPhase _tlsPhase;

    private readonly MemoryStream _heldOutput = new MemoryStream();

    // The most that is kept for a STARTTLS that has not happened yet: a server that never answers must not make the
    // typed text pile up for ever (a write beyond it fails and the user is told).
    internal const int MaxHeldOutputBytes = 64 * 1024;

    // How long the server may take to start STARTTLS (0: no limit), and the timer that watches it.
    private readonly int _startTlsTimeoutMs;

    private Timer _startTlsTimer;

    private byte[] _tlsLeftover;

    private string _fatalError;

    // The output queue (see the class comment). _sendLock may be taken while _lock is held, never the other way round.
    private readonly object _sendLock = new object();

    private readonly MemoryStream _sendQueue = new MemoryStream();

    private bool _sending;

    private Exception _sendFailure;

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
        _acceptEnvironmentRequests = options.AcceptEnvironmentRequests;
        _charset = NormalizeCharsetName(options.Charset);
        // Without a way to make the handshake the option can only be refused.
        _upgradeToTls = options.UpgradeToTls;
        _startTlsMode = _upgradeToTls == null ? TelnetStartTlsMode.Off : options.StartTls;
        _startTlsTimeoutMs = Math.Max(0, options.StartTlsTimeoutMs);
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

    /// <summary>True once the session runs inside TLS negotiated in-band (STARTTLS).</summary>
    public bool StartTlsActive => _tlsPhase == TlsPhase.Done;

    /// <summary>The stream the Telnet data travels in: the original one, or the encrypted one after STARTTLS.</summary>
    public Stream InnerStream => _inner;

    /// <summary>The character set agreed on with the server (option CHARSET); null when there was no agreement.</summary>
    public string AcceptedCharset
    {
        get
        {
            lock (_lock)
            {
                return _acceptedCharset;
            }
        }
    }

    /// <summary>
    /// Makes the first move: offers START_TLS when it is required, and, when active negotiation was asked for,
    /// SUPPRESS-GO-AHEAD (both ways), the terminal type and the window size. Does nothing in the default,
    /// answer-only mode. Call once, right after connecting.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            MemoryStream offer = new MemoryStream();
            if (_startTlsMode == TelnetStartTlsMode.Require)
            {
                // The rest is offered inside TLS: nothing about the session is said before the handshake.
                OfferLocal(offer, OptStartTls, true);
            }
            else if (_activeNegotiation)
            {
                WriteActiveOffers(offer);
            }
            QueueCommand(offer.ToArray());
        }
        if (_startTlsMode == TelnetStartTlsMode.Require)
        {
            WatchStartTls();
        }
        Drain(wait: true);
    }

    /// <summary>
    /// True while STARTTLS is required and not done: nothing of the session may be sent in clear text until then (a
    /// keep-alive, for one, has to wait).
    /// </summary>
    internal bool IsAwaitingRequiredTls => _startTlsMode == TelnetStartTlsMode.Require && _tlsPhase != TlsPhase.Done;

    // Gives the server a limited time to start STARTTLS. The handshake itself is not timed here (it has a limit of its
    // own); the connection is closed, with the reason, when the server has not got that far by then.
    private void WatchStartTls()
    {
        if (_startTlsTimeoutMs <= 0 || _startTlsTimer != null)
        {
            return;
        }
        _startTlsTimer = new Timer(_ =>
        {
            lock (_lock)
            {
                bool waiting = _tlsPhase == TlsPhase.FollowsSent || (_tlsPhase == TlsPhase.None && _startTlsMode == TelnetStartTlsMode.Require);
                if (!waiting)
                {
                    return;
                }
                _fatalError ??= "The server did not start STARTTLS (Telnet option START_TLS) within " + (_startTlsTimeoutMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                    + " s. It may not support it; what was typed meanwhile was not sent.";
            }
            try
            {
                // Wakes up the reading thread, which then reports the reason.
                _inner.Dispose();
            }
            catch
            {
            }
        }, null, _startTlsTimeoutMs, Timeout.Infinite);
    }

    // The offers of active negotiation. The caller holds _lock.
    private void WriteActiveOffers(MemoryStream offer)
    {
        if (!_activeNegotiation)
        {
            return;
        }
        _remote[OptSuppressGoAhead] = OptionState.Requested;
        _local[OptSuppressGoAhead] = OptionState.Requested;
        _local[OptTerminalType] = OptionState.Requested;
        _local[OptNaws] = OptionState.Requested;
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
            QueueCommand(new[] { IAC, SB, OptStatus, SubSend, IAC, SE });
        }
        Drain(wait: false);
        return true;
    }

    /// <summary>The options in force on our side and on the server's side, as text for the user.</summary>
    public string DescribeOptions()
    {
        lock (_lock)
        {
            string text = "We do:\r\n" + DescribeOptions(_local) + "\r\n\r\nThe server does:\r\n" + DescribeOptions(_remote);
            if (_acceptedCharset != null)
            {
                text += "\r\n\r\nCharacter set agreed on: " + _acceptedCharset;
            }
            return text;
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
            case OptSuppressGoAhead: return "SUPPRESS-GO-AHEAD";
            case OptStatus: return "STATUS";
            case OptTimingMark: return "TIMING-MARK";
            case 18: return "LOGOUT";
            case OptTerminalType: return "TERMINAL-TYPE";
            case 25: return "END-OF-RECORD";
            case OptNaws: return "NAWS";
            case OptTSpeed: return "TSPEED";
            case 33: return "TOGGLE-FLOW-CONTROL";
            case OptLineMode: return "LINEMODE";
            case OptXDispLoc: return "XDISPLOC";
            case 36: return "ENVIRON";
            case 37: return "AUTHENTICATION";
            case 38: return "ENCRYPT";
            case OptNewEnviron: return "NEW-ENVIRON";
            case OptCharset: return "CHARSET";
            case 44: return "COM-PORT";
            case OptStartTls: return "START-TLS";
            default: return "option " + option;
        }
    }

    /// <summary>Sends a Telnet command (Break, Interrupt Process, Are You There, NOP, ...).</summary>
    public void SendCommand(byte command)
    {
        lock (_lock)
        {
            if (IsAwaitingRequiredTls)
            {
                throw new InvalidOperationException("The connection is waiting for STARTTLS: nothing is sent in clear text until it is done.");
            }
            QueueCommand(new[] { IAC, command });
        }
        Drain(wait: false);
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
            if (_local[OptNaws] != OptionState.On)
            {
                return;
            }
            QueueCommand(BuildWindowSizeMessage());
        }
        Drain(wait: false);
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
            int received;
            try
            {
                if (_tlsPhase == TlsPhase.HandshakeDue)
                {
                    UpgradeToTls();
                }
                // At most 'count' bytes are taken from the connection and a byte never grows when the commands are
                // removed, so the result always fits and nothing has to be kept for the next call.
                received = _inner.Read(_readBuffer, 0, Math.Min(count, _readBuffer.Length));
            }
            catch (Exception ex) when (_fatalError != null)
            {
                // The connection was closed because of a reason of ours (the STARTTLS time ran out): that is the reason to show.
                throw new IOException(_fatalError, ex);
            }
            if (received <= 0)
            {
                if (_fatalError != null)
                {
                    throw new IOException(_fatalError);
                }
                return 0;
            }
            int produced;
            bool echoChanged;
            bool statusArrived;
            string fatalError;
            lock (_lock)
            {
                produced = Process(_readBuffer, received, buffer, offset, out int consumed);
                if (_tlsPhase == TlsPhase.HandshakeDue && consumed < received)
                {
                    // What follows the server's FOLLOWS is already TLS: it belongs to the handshake.
                    _tlsLeftover = new byte[received - consumed];
                    Buffer.BlockCopy(_readBuffer, consumed, _tlsLeftover, 0, _tlsLeftover.Length);
                }
                FlushReplies();
                bool echoNow = _remote[OptEcho] == OptionState.On;
                echoChanged = echoNow != _echoReported;
                _echoReported = echoNow;
                statusArrived = _remoteStatusArrived;
                _remoteStatusArrived = false;
                fatalError = _fatalError;
            }
            // Never waits: see the class comment.
            Drain(wait: false);
            if (fatalError != null)
            {
                throw new IOException(fatalError);
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
    // thread, outside the lock, and goes through the same path as the user's own typing (without waiting for it to
    // be sent: the reading thread must go on reading).
    private void AnswerPrompts(byte[] buffer, int offset, int count)
    {
        try
        {
            foreach (string line in _autoLogin.Feed(buffer, offset, count))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                WriteData(bytes, 0, bytes.Length, wait: false);
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
        WriteData(buffer, offset, count, wait: true);
    }

    private void WriteData(byte[] buffer, int offset, int count, bool wait)
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
                QueueData(encoded);
            }
        }
        Drain(wait);
    }

    public override void Flush()
    {
        Drain(wait: true);
        lock (_sendLock)
        {
            if (!_sending)
            {
                _inner.Flush();
            }
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
            _startTlsTimer?.Dispose();
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
        int end = offset + count;
        int extra = 0;
        for (int i = offset; i < end; i++)
        {
            byte b = buffer[i];
            if (b == IAC || (b == CR && !binary && !(i + 1 < end && (buffer[i + 1] == LF || buffer[i + 1] == NUL))))
            {
                extra++;
            }
        }
        byte[] result = new byte[count + extra];
        if (extra == 0)
        {
            // The usual case: plain text goes out as it is.
            Buffer.BlockCopy(buffer, offset, result, 0, count);
            return result;
        }
        int position = 0;
        for (int i = offset; i < end; i++)
        {
            byte b = buffer[i];
            result[position++] = b;
            if (b == IAC)
            {
                result[position++] = IAC;
            }
            else if (b == CR && !binary && !(i + 1 < end && (buffer[i + 1] == LF || buffer[i + 1] == NUL)))
            {
                result[position++] = NUL;
            }
        }
        return result;
    }

    // --- the output queue ---

    // True while what the user types must wait: STARTTLS is being negotiated, or it is required and not done yet.
    private bool IsHoldingOutput => _tlsPhase == TlsPhase.FollowsSent || _tlsPhase == TlsPhase.HandshakeDue
        || (_startTlsMode == TelnetStartTlsMode.Require && _tlsPhase != TlsPhase.Done);

    // The user's data (typed text, the automatic login). The caller holds _lock.
    private void QueueData(byte[] bytes)
    {
        if (IsHoldingOutput)
        {
            if (_heldOutput.Length + bytes.Length > MaxHeldOutputBytes)
            {
                throw new IOException("The server has not started STARTTLS yet, and too much has been typed meanwhile: " + _heldOutput.Length + " bytes are waiting for it.");
            }
            _heldOutput.Write(bytes, 0, bytes.Length);
            return;
        }
        QueueWire(bytes);
    }

    // Commands and negotiation. Between the FOLLOWS and the end of the handshake nothing may be sent, and what
    // would be sent then (a keep-alive NOP, a window size) is of no use afterwards. The caller holds _lock.
    private void QueueCommand(byte[] bytes)
    {
        if (bytes.Length == 0 || _tlsPhase == TlsPhase.FollowsSent || _tlsPhase == TlsPhase.HandshakeDue)
        {
            return;
        }
        QueueWire(bytes);
    }

    private void QueueWire(byte[] bytes)
    {
        lock (_sendLock)
        {
            _sendQueue.Write(bytes, 0, bytes.Length);
        }
    }

    // Writes the queue to the connection. Only one thread writes at a time; one that finds another one writing
    // either waits until all is sent (wait: the user's writes, so a fast typist or a paste is slowed down to what the
    // connection takes) or leaves its bytes to that thread (the reading thread). Must be called without _lock.
    private void Drain(bool wait)
    {
        lock (_sendLock)
        {
            while (_sending)
            {
                if (!wait)
                {
                    return;
                }
                Monitor.Wait(_sendLock);
            }
            if (_sendQueue.Length == 0)
            {
                if (wait && _sendFailure != null)
                {
                    // The data of this caller was thrown away when the connection broke.
                    throw new IOException("The connection is broken: " + _sendFailure.Message, _sendFailure);
                }
                return;
            }
            _sending = true;
        }
        try
        {
            while (true)
            {
                byte[] chunk;
                Stream inner;
                lock (_sendLock)
                {
                    if (_sendQueue.Length == 0)
                    {
                        _sending = false;
                        Monitor.PulseAll(_sendLock);
                        return;
                    }
                    chunk = _sendQueue.ToArray();
                    _sendQueue.SetLength(0);
                    inner = _inner;
                }
                inner.Write(chunk, 0, chunk.Length);
            }
        }
        catch (Exception exception)
        {
            lock (_sendLock)
            {
                _sendFailure = exception;
                _sendQueue.SetLength(0);
                _sending = false;
                Monitor.PulseAll(_sendLock);
            }
            if (wait)
            {
                throw;
            }
            // The reading thread learns about a broken connection from its next read.
        }
    }

    // --- STARTTLS ---

    // Runs on the reading thread once the server's FOLLOWS arrived: the handshake, then a fresh start of the option
    // negotiation inside TLS. A failed handshake ends the connection (the exception goes to the reader).
    private void UpgradeToTls()
    {
        // Our FOLLOWS, and everything queued before it, has to be on the wire before the first byte of TLS.
        Drain(wait: true);
        Stream raw = _inner;
        byte[] leftover = _tlsLeftover;
        _tlsLeftover = null;
        Stream secure = _upgradeToTls(leftover != null ? new PrefixedStream(leftover, raw) : raw);
        lock (_lock)
        {
            lock (_sendLock)
            {
                _inner = secure;
            }
            _tlsPhase = TlsPhase.Done;
            _startTlsTimer?.Dispose();
            ResetNegotiation();
            MemoryStream offer = new MemoryStream();
            WriteActiveOffers(offer);
            QueueCommand(offer.ToArray());
            if (_heldOutput.Length > 0)
            {
                QueueWire(_heldOutput.ToArray());
                _heldOutput.SetLength(0);
            }
        }
        Drain(wait: false);
    }

    // After STARTTLS every option starts again from its default (off); only START_TLS itself stays on, so a
    // repeated DO START_TLS is not answered. The caller holds _lock.
    private void ResetNegotiation()
    {
        Array.Clear(_remote, 0, _remote.Length);
        Array.Clear(_local, 0, _local.Length);
        _local[OptStartTls] = OptionState.On;
        _lineModeMask = 0;
        _lineModeSeen = false;
        _line.SetLength(0);
        _skipLineFeed = false;
        _escapeState = 0;
        SetDefaultSpecialCharacters();
        _acceptedCharset = null;
        _previousWasCr = false;
        _state = ParseState.Data;
    }

    // Announces that the TLS handshake follows. From now on nothing but this is sent until the handshake is done.
    // The caller holds _lock.
    private void SendFollows()
    {
        Reply(IAC, SB, OptStartTls, StartTlsFollows, IAC, SE);
        _tlsPhase = TlsPhase.FollowsSent;
        // Also when STARTTLS is only accepted: a server that agreed and then says nothing must not hold the session for ever.
        WatchStartTls();
    }

    // --- reading ---

    // Runs the received bytes through the state machine. Data goes to 'output', answers are queued in
    // _pendingReplies. Returns the number of data bytes produced; 'consumed' is the number of input bytes used (less
    // than 'length' only when the TLS handshake has to start right after the server's FOLLOWS). The caller holds _lock.
    private int Process(byte[] input, int length, byte[] output, int outputOffset, out int consumed)
    {
        int produced = 0;
        int i = 0;
        while (i < length)
        {
            byte b;
            if (_state == ParseState.Data)
            {
                // The text between two commands is copied in one tight loop.
                bool binaryIn = _remote[OptBinary] == OptionState.On;
                while (i < length)
                {
                    b = input[i];
                    if (b == IAC)
                    {
                        break;
                    }
                    i++;
                    if (b == NUL && _previousWasCr && !binaryIn)
                    {
                        // CR NUL is a bare carriage return: the NUL is only there to tell it from CR LF.
                        _previousWasCr = false;
                        continue;
                    }
                    output[outputOffset + produced++] = b;
                    _previousWasCr = b == CR;
                }
                if (i >= length)
                {
                    break;
                }
                _state = ParseState.Iac;
                i++;
                continue;
            }
            b = input[i++];
            switch (_state)
            {
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
                        _state = ParseState.Data;
                        OnSubnegotiation();
                        if (_tlsPhase == TlsPhase.HandshakeDue)
                        {
                            // The rest is TLS: the handshake takes it.
                            consumed = i;
                            return FinishProduced(output, outputOffset, produced);
                        }
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
        consumed = length;
        return FinishProduced(output, outputOffset, produced);
    }

    // While STARTTLS is required and not done, the server has nothing to say yet: text from it is a server that
    // ignores the option, or someone who strips it from the connection. Line ends and blanks are tolerated (and
    // dropped), anything else ends the connection.
    private int FinishProduced(byte[] output, int outputOffset, int produced)
    {
        if (produced == 0 || _startTlsMode != TelnetStartTlsMode.Require || _tlsPhase == TlsPhase.Done)
        {
            return produced;
        }
        for (int i = 0; i < produced; i++)
        {
            byte b = output[outputOffset + i];
            if (b != CR && b != LF && b != NUL && b != (byte)' ')
            {
                _fatalError ??= "The server sent text before the TLS handshake (STARTTLS), which the connection requires. " +
                    "The server does not support STARTTLS, or something on the way removed it.";
                break;
            }
        }
        return 0;
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

    private bool AcceptsFromServer(byte option)
    {
        return option == OptEcho || option == OptSuppressGoAhead || option == OptBinary || option == OptStatus
            || (option == OptCharset && _charset != null);
    }

    private bool AcceptsFromUs(byte option)
    {
        return option == OptSuppressGoAhead || option == OptTerminalType || option == OptNaws || option == OptBinary
            || option == OptStatus
            || (option == OptLineMode && _allowLineMode)
            || (option == OptTSpeed && _terminalSpeed != null)
            || (option == OptXDispLoc && _xDisplayLocation != null)
            || (option == OptNewEnviron && (_environment.Count > 0 || _acceptEnvironmentRequests))
            || (option == OptCharset && _charset != null)
            || (option == OptStartTls && _startTlsMode != TelnetStartTlsMode.Off && _tlsPhase == TlsPhase.None);
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
        if (option == OptTimingMark)
        {
            // Not a state but a question ("have you got this far?"): it is answered every time it is asked, and
            // since everything before it has been handled here, at once.
            Reply(IAC, WILL, OptTimingMark);
            return;
        }
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
            else if (option == OptStartTls)
            {
                SendFollows();
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
                if (option == OptStartTls)
                {
                    // First: the phase decides whether anything may be sent at all.
                    OnStartTlsRefused();
                }
                Reply(IAC, WONT, option);
                if (option == OptLineMode)
                {
                    LeaveLineMode();
                }
                break;
            case OptionState.Requested:
                _local[option] = OptionState.Off;
                if (option == OptStartTls)
                {
                    OnStartTlsRefused();
                }
                break;
        }
    }

    // The server does not want STARTTLS after all. Required: the connection ends. Otherwise the session goes on
    // without TLS and what was held back is sent.
    private void OnStartTlsRefused()
    {
        if (_tlsPhase == TlsPhase.Done)
        {
            return;
        }
        _tlsPhase = TlsPhase.None;
        if (_startTlsMode == TelnetStartTlsMode.Require)
        {
            _fatalError ??= "The server refused STARTTLS (Telnet option START_TLS), which the connection requires.";
            return;
        }
        if (_heldOutput.Length > 0)
        {
            byte[] held = _heldOutput.ToArray();
            _heldOutput.SetLength(0);
            _pendingReplies.Write(held, 0, held.Length);
        }
    }

    private void OnSubnegotiation()
    {
        if (_subLength < 1)
        {
            return;
        }
        byte command = _subData[0];
        switch (_subOption)
        {
            case OptTerminalType:
                if (_local[OptTerminalType] == OptionState.On && command == TerminalTypeSend)
                {
                    ReplyText(OptTerminalType, _terminalType);
                }
                break;
            case OptTSpeed:
                if (_local[OptTSpeed] == OptionState.On && command == SubSend)
                {
                    ReplyText(OptTSpeed, _terminalSpeed);
                }
                break;
            case OptXDispLoc:
                if (_local[OptXDispLoc] == OptionState.On && command == SubSend)
                {
                    ReplyText(OptXDispLoc, _xDisplayLocation);
                }
                break;
            case OptNewEnviron:
                if (_local[OptNewEnviron] == OptionState.On && command == EnvSend)
                {
                    OnEnvironmentRequest();
                }
                break;
            case OptStatus:
                if (_local[OptStatus] == OptionState.On && command == SubSend)
                {
                    ReplyStatus();
                }
                else if (_remote[OptStatus] == OptionState.On && command == SubIs)
                {
                    _remoteStatus = ParseStatus(_subData, 1, _subLength - 1);
                    _remoteStatusArrived = true;
                }
                break;
            case OptCharset:
                if (_local[OptCharset] == OptionState.On || _remote[OptCharset] == OptionState.On)
                {
                    OnCharsetSubnegotiation(command);
                }
                break;
            case OptStartTls:
                if (command == StartTlsFollows && _tlsPhase == TlsPhase.FollowsSent)
                {
                    _tlsPhase = TlsPhase.HandshakeDue;
                }
                break;
            case OptLineMode:
                if (_local[OptLineMode] != OptionState.On)
                {
                    break;
                }
                switch (command)
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
                break;
        }
        // Other subnegotiations are not used by the options we accepted.
    }

    // CHARSET (RFC 2066): REQUEST [ "[TTABLE]" version ] <separator> name <separator> name ... The terminal's own
    // character set is accepted when the server lists it (under any of its usual names), otherwise the request is
    // rejected and both sides go on as before. A translation table is never accepted.
    private void OnCharsetSubnegotiation(byte command)
    {
        if (command == CharsetTTableIs)
        {
            Reply(IAC, SB, OptCharset, CharsetTTableRejected, IAC, SE);
            return;
        }
        if (command != CharsetRequest)
        {
            // ACCEPTED / REJECTED answer requests we never make.
            return;
        }
        int i = 1;
        const string TTable = "[TTABLE]";
        if (_subLength - i >= TTable.Length + 1 && Encoding.ASCII.GetString(_subData, i, TTable.Length) == TTable)
        {
            i += TTable.Length + 1;
        }
        string accepted = null;
        if (i < _subLength)
        {
            byte separator = _subData[i++];
            int start = i;
            for (; i <= _subLength && accepted == null; i++)
            {
                if (i == _subLength || _subData[i] == separator)
                {
                    string name = Encoding.ASCII.GetString(_subData, start, i - start).Trim();
                    if (name.Length > 0 && NormalizeCharsetName(name) == _charset)
                    {
                        accepted = name;
                    }
                    start = i + 1;
                }
            }
        }
        if (accepted == null)
        {
            Reply(IAC, SB, OptCharset, CharsetRejected, IAC, SE);
            return;
        }
        _acceptedCharset = accepted;
        Reply(IAC, SB, OptCharset, CharsetAccepted);
        foreach (byte b in Encoding.ASCII.GetBytes(accepted))
        {
            WriteEscaped(_pendingReplies, b);
        }
        Reply(IAC, SE);
    }

    /// <summary>
    /// A character set name in one spelling for comparison: lower case, without '-', '_' and blanks, with the usual
    /// aliases of the terminal's encodings (cp1251 / windows-1251, ibm866 / cp866, ascii / us-ascii, ...). Null for
    /// an empty name.
    /// </summary>
    internal static string NormalizeCharsetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        StringBuilder text = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (c != '-' && c != '_' && c != ' ')
            {
                text.Append(c);
            }
        }
        string normalized = text.ToString();
        switch (normalized)
        {
            case "cp1251":
            case "xcp1251":
                return "windows1251";
            case "ibm866":
            case "866":
                return "cp866";
            case "ascii":
            case "usascii":
            case "ansix3.41968":
                return "usascii";
            case "iso88595":
            case "isoir144":
            case "cyrillic":
                return "iso88595";
            case "koi8r":
            case "koi8":
                return "koi8r";
            case "utf8":
                return "utf8";
            default:
                return normalized;
        }
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
                AddSlc(answer, SlcIp, SlcVariable, DefaultSlcIp);
                AddSlc(answer, SlcAbort, SlcVariable, DefaultSlcAbort);
                AddSlc(answer, SlcEof, SlcVariable, DefaultSlcEof);
                AddSlc(answer, SlcSusp, SlcVariable, DefaultSlcSusp);
                AddSlc(answer, SlcEc, SlcVariable, DefaultSlcEc);
                AddSlc(answer, SlcEl, SlcVariable, DefaultSlcEl);
                AddSlc(answer, SlcEw, SlcVariable, DefaultSlcEw);
                SetDefaultSpecialCharacters();
                continue;
            }
            if (!IsKnownSpecialCharacter(function))
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

    private static bool IsKnownSpecialCharacter(byte function)
    {
        return function == SlcIp || function == SlcAbort || function == SlcEof || function == SlcSusp
            || function == SlcEc || function == SlcEl || function == SlcEw;
    }

    private void SetDefaultSpecialCharacters()
    {
        _slcIp = DefaultSlcIp;
        _slcAbort = DefaultSlcAbort;
        _slcEof = DefaultSlcEof;
        _slcSusp = DefaultSlcSusp;
        _slcEc = DefaultSlcEc;
        _slcEl = DefaultSlcEl;
        _slcEw = DefaultSlcEw;
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
            case SlcEof:
                _slcEof = character;
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

    private static void AddSlc(MemoryStream answer, byte function, byte flags, int value)
    {
        WriteEscaped(answer, function);
        WriteEscaped(answer, flags);
        WriteEscaped(answer, (byte)value);
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
        if (_line.Length > 0)
        {
            byte[] encoded = EncodeOutgoing(_line.GetBuffer(), 0, (int)_line.Length, binary: false);
            output.Write(encoded, 0, encoded.Length);
            _line.SetLength(0);
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
            bool signal = b == _slcIp || b == _slcAbort || b == _slcSusp;
            if (trap && signal)
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
            if (signal)
            {
                // Without TRAPSIG the server handles the signal characters itself: one must not wait in the line
                // until Enter is pressed. It goes out at once, after what was typed before it.
                FlushLine(output, newLine: false);
                passThrough.WriteByte(b);
                FlushPassThrough(passThrough, output, binary);
                continue;
            }
            if (b == _slcEof)
            {
                // As in a terminal driver: what was typed goes out without a line end, on an empty line EOF does.
                if (_line.Length > 0)
                {
                    FlushLine(output, newLine: false);
                }
                else
                {
                    output.WriteByte(IAC);
                    output.WriteByte(Eof);
                }
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
        byte[] encoded = EncodeOutgoing(passThrough.GetBuffer(), 0, (int)passThrough.Length, binary);
        passThrough.SetLength(0);
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

    // IAC SB <option> IS <text> IAC SE (TERMINAL-TYPE, TSPEED, XDISPLOC): the text is plain ASCII.
    private void ReplyText(byte option, string text)
    {
        Reply(IAC, SB, option, SubIs);
        foreach (byte b in Encoding.ASCII.GetBytes(text))
        {
            WriteEscaped(_pendingReplies, b);
        }
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

    // STATUS IS: our side of every option that is on (WILL) and the server's side (DO), then the parameters of the
    // subnegotiated options we use, as "SB <option> <parameters> SE": the window size, the terminal type, speed and
    // display, and the line mode. In the list SE (also as a value) is doubled, and IAC is doubled as everywhere.
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
        if (_local[OptTerminalType] == OptionState.On)
        {
            WriteStatusParameters(status, OptTerminalType, Concat(TerminalTypeIs, Encoding.ASCII.GetBytes(_terminalType)));
        }
        if (_local[OptNaws] == OptionState.On)
        {
            WriteStatusParameters(status, OptNaws, new[] { (byte)(_columns >> 8), (byte)(_columns & 0xFF), (byte)(_rows >> 8), (byte)(_rows & 0xFF) });
        }
        if (_local[OptTSpeed] == OptionState.On)
        {
            WriteStatusParameters(status, OptTSpeed, Concat(SubIs, Encoding.ASCII.GetBytes(_terminalSpeed)));
        }
        if (_local[OptXDispLoc] == OptionState.On)
        {
            WriteStatusParameters(status, OptXDispLoc, Concat(SubIs, Encoding.ASCII.GetBytes(_xDisplayLocation)));
        }
        if (_local[OptLineMode] == OptionState.On && _lineModeSeen)
        {
            WriteStatusParameters(status, OptLineMode, new[] { LmMode, _lineModeMask });
        }
        Reply(IAC, SB, OptStatus, SubIs);
        byte[] bytes = status.ToArray();
        _pendingReplies.Write(bytes, 0, bytes.Length);
        Reply(IAC, SE);
    }

    private static void WriteStatusParameters(MemoryStream status, byte option, byte[] parameters)
    {
        status.WriteByte(SB);
        WriteStatusByte(status, option);
        foreach (byte b in parameters)
        {
            WriteStatusByte(status, b);
        }
        status.WriteByte(SE);
    }

    private static void WriteStatusByte(MemoryStream status, byte b)
    {
        status.WriteByte(b);
        if (b == IAC || b == SE)
        {
            status.WriteByte(b);
        }
    }

    private static byte[] Concat(byte first, byte[] rest)
    {
        byte[] result = new byte[rest.Length + 1];
        result[0] = first;
        Buffer.BlockCopy(rest, 0, result, 1, rest.Length);
        return result;
    }

    // Reads the server's STATUS IS: WILL / WONT / DO / DONT <option> entries, and SB <option> ... SE for an option with
    // parameters (the known ones are shown readably, the others as bytes). The entries are returned one per line.
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
                if (option == SE && i < end && data[i] == SE)
                {
                    i++;
                }
                MemoryStream parameters = new MemoryStream();
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
                    parameters.WriteByte(b);
                }
                entries.Add("SB " + OptionName(option) + DescribeParameters(option, parameters.ToArray()));
            }
        }
        return entries.Count == 0 ? "(no options)" : string.Join("\r\n", entries.ToArray());
    }

    private static string DescribeParameters(byte option, byte[] parameters)
    {
        if (parameters.Length == 0)
        {
            return "";
        }
        if (option == OptNaws && parameters.Length == 4)
        {
            return " " + ((parameters[0] << 8) | parameters[1]) + "x" + ((parameters[2] << 8) | parameters[3]);
        }
        if ((option == OptTerminalType || option == OptTSpeed || option == OptXDispLoc) && parameters[0] == SubIs && IsPrintable(parameters, 1))
        {
            return " IS " + Encoding.ASCII.GetString(parameters, 1, parameters.Length - 1);
        }
        if (option == OptLineMode && parameters.Length == 2 && parameters[0] == LmMode)
        {
            List<string> bits = new List<string>();
            if ((parameters[1] & ModeEdit) != 0)
            {
                bits.Add("EDIT");
            }
            if ((parameters[1] & ModeTrapSig) != 0)
            {
                bits.Add("TRAPSIG");
            }
            return " MODE " + (bits.Count == 0 ? "0" : string.Join(" ", bits.ToArray()));
        }
        StringBuilder text = new StringBuilder();
        int shown = Math.Min(parameters.Length, 32);
        for (int i = 0; i < shown; i++)
        {
            text.Append(' ').Append(parameters[i].ToString("X2"));
        }
        if (shown < parameters.Length)
        {
            text.Append(" ... (" + parameters.Length + " bytes)");
        }
        return text.ToString();
    }

    private static bool IsPrintable(byte[] data, int start)
    {
        for (int i = start; i < data.Length; i++)
        {
            if (data[i] < 0x20 || data[i] > 0x7E)
            {
                return false;
            }
        }
        return true;
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
        if (_tlsPhase == TlsPhase.FollowsSent || _tlsPhase == TlsPhase.HandshakeDue)
        {
            // After our FOLLOWS nothing but the TLS handshake may be sent.
            return;
        }
        _pendingReplies.Write(bytes, 0, bytes.Length);
    }

    // Queues the answers of the chunk just processed; the caller sends them (Drain) once it has left the lock.
    // The caller holds _lock.
    private void FlushReplies()
    {
        if (_pendingReplies.Length == 0)
        {
            return;
        }
        byte[] replies = _pendingReplies.ToArray();
        _pendingReplies.SetLength(0);
        // The replies end with our FOLLOWS when STARTTLS was just agreed: they still go out, as the last thing before
        // the handshake.
        QueueWire(replies);
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

/// <summary>Whether TLS is negotiated inside the Telnet session (option START_TLS).</summary>
internal enum TelnetStartTlsMode
{
    /// <summary>The option is refused.</summary>
    Off,

    /// <summary>Agreed to when the server asks for it; without it the session runs unencrypted.</summary>
    Accept,

    /// <summary>Offered at once; the connection ends when the server refuses it or says anything before it.</summary>
    Require
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
    /// Empty (and no X display location): the option is refused, unless <see cref="AcceptEnvironmentRequests"/>.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> Environment { get; set; }

    /// <summary>
    /// Agree to NEW-ENVIRON even without variables: the server's request is then answered with "not defined", as the
    /// telnet clients of Unix do, instead of refusing the option.
    /// </summary>
    public bool AcceptEnvironmentRequests { get; set; }

    /// <summary>
    /// The character set of the terminal ("utf-8", "koi8-r", ...): accepted when the server proposes it (option
    /// CHARSET). Empty: the option is refused.
    /// </summary>
    public string Charset { get; set; }

    /// <summary>Whether TLS is negotiated in the session (option START_TLS); needs <see cref="UpgradeToTls"/>.</summary>
    public TelnetStartTlsMode StartTls { get; set; }

    /// <summary>How long the server may take to start STARTTLS, in milliseconds; 0: no limit.</summary>
    public int StartTlsTimeoutMs { get; set; }

    /// <summary>
    /// Makes the TLS handshake of STARTTLS as a client over the given stream and returns the encrypted stream; throws
    /// when it fails. Null: STARTTLS is refused.
    /// </summary>
    public Func<Stream, Stream> UpgradeToTls { get; set; }
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
