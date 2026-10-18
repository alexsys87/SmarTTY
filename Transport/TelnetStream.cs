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
///   TERMINAL-TYPE (24)                 - answers with the configured terminal type,
///   NAWS (31)                          - reports the terminal size, again whenever it changes.
/// We only ever answer: no option is offered on our own initiative, so a server that does not speak Telnet is not
/// confronted with unexpected commands.
///
/// One thread may read while another one writes. All the protocol state, and every write to the underlying
/// stream, is guarded by one lock, so a negotiation reply never lands in the middle of the user's data.
/// </summary>
internal sealed class TelnetStream : Stream
{
    // Commands (RFC 854).
    internal const byte SE = 240;
    internal const byte SB = 250;
    internal const byte WILL = 251;
    internal const byte WONT = 252;
    internal const byte DO = 253;
    internal const byte DONT = 254;
    internal const byte IAC = 255;

    // Options.
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

    private readonly Stream _inner;

    private readonly string _terminalType;

    // Guards everything below and every write to _inner.
    private readonly object _lock = new object();

    private readonly byte[] _readBuffer = new byte[4096];

    private readonly MemoryStream _pendingReplies = new MemoryStream();

    // The server's side of an option is on (we answered DO) / our side of an option is on (we answered WILL).
    private readonly bool[] _remoteEnabled = new bool[256];

    private readonly bool[] _localEnabled = new bool[256];

    private readonly byte[] _subData = new byte[MaxSubnegotiationLength];

    private ParseState _state = ParseState.Data;

    private byte _subOption;

    private int _subLength;

    private bool _previousWasCr;

    private int _columns;

    private int _rows;

    public TelnetStream(Stream inner, string terminalType = DefaultTerminalType, int columns = DefaultColumns, int rows = DefaultRows)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _terminalType = string.IsNullOrEmpty(terminalType) ? DefaultTerminalType : terminalType;
        _columns = ClampSize(columns, DefaultColumns);
        _rows = ClampSize(rows, DefaultRows);
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
                return _localEnabled[OptNaws];
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
            if (_localEnabled[OptNaws])
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
            lock (_lock)
            {
                produced = Process(_readBuffer, received, buffer, offset);
                FlushReplies();
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
        byte[] encoded = EncodeOutgoing(buffer, offset, count);
        lock (_lock)
        {
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
            else if (b == CR)
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
                    else if (b == NUL && _previousWasCr)
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
    // clients that both answer everything from talking to each other forever.

    private void OnWill(byte option)
    {
        if (_remoteEnabled[option])
        {
            return;
        }
        if (option == OptEcho || option == OptSuppressGoAhead)
        {
            _remoteEnabled[option] = true;
            Reply(IAC, DO, option);
        }
        else
        {
            Reply(IAC, DONT, option);
        }
    }

    private void OnWont(byte option)
    {
        if (_remoteEnabled[option])
        {
            _remoteEnabled[option] = false;
            Reply(IAC, DONT, option);
        }
    }

    private void OnDo(byte option)
    {
        if (_localEnabled[option])
        {
            return;
        }
        if (option == OptSuppressGoAhead || option == OptTerminalType || option == OptNaws)
        {
            _localEnabled[option] = true;
            Reply(IAC, WILL, option);
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
        if (_localEnabled[option])
        {
            _localEnabled[option] = false;
            Reply(IAC, WONT, option);
        }
    }

    private void OnSubnegotiation()
    {
        if (_subOption == OptTerminalType && _localEnabled[OptTerminalType] && _subLength >= 1 && _subData[0] == TerminalTypeSend)
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
