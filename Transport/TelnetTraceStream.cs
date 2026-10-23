using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SmarTTY.Transport;

/// <summary>
/// A diagnostic record of everything that goes over a telnet connection, for finding out why a device does not behave
/// as expected (an automatic login that does not get through, for example). It is off unless the environment variable
/// <see cref="EnvironmentVariable"/> names a file; it does not change what is sent or received.
///
/// Every read and write is written as one record: the time since the connection was made, the direction (RX from the
/// device, TX to it), the bytes in hex and the same bytes as text. What is sent is masked, because it holds the user
/// name and the password: printable characters show as '*', only control bytes (CR, LF, NUL, the telnet commands)
/// are shown. What the device sends is shown as it is.
/// </summary>
internal sealed class TelnetTraceStream : Stream
{
    public const string EnvironmentVariable = "SMARTTY_TELNET_TRACE";

    private const int BytesPerLine = 32;

    private readonly Stream _inner;

    private readonly TextWriter _log;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly object _logLock = new object();

    internal TelnetTraceStream(Stream inner, TextWriter log)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// The stream itself when no trace was asked for (or the file cannot be written), otherwise the stream wrapped in a
    /// recording one. Never throws: a diagnostic aid must not break the connection it observes.
    /// </summary>
    public static Stream WrapIfRequested(Stream inner, string endpoint)
    {
        string path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return inner;
        }
        try
        {
            StreamWriter writer = new StreamWriter(new FileStream(path.Trim(), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            writer.WriteLine("# SmarTTY telnet trace, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", " + endpoint);
            writer.WriteLine("# RX is what the device sends, TX what is sent to it. Sent printable characters (user name, password) are masked as '*'.");
            return new TelnetTraceStream(inner, writer);
        }
        catch (Exception)
        {
            return inner;
        }
    }

    /// <summary>Adds a remark of this program to the record (what the automatic login did, for example).</summary>
    public void Annotate(string text)
    {
        Note(text);
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int received;
        try
        {
            received = _inner.Read(buffer, offset, count);
        }
        catch (Exception exception)
        {
            Note("read failed: " + exception.Message);
            throw;
        }
        if (received > 0)
        {
            Record("RX", buffer, offset, received, mask: false);
        }
        else
        {
            Note("the device closed the connection");
        }
        return received;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        // Recorded first: the time is the one the data was handed to the connection.
        Record("TX", buffer, offset, count, mask: true);
        try
        {
            _inner.Write(buffer, offset, count);
        }
        catch (Exception exception)
        {
            Note("write failed: " + exception.Message);
            throw;
        }
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Note("connection closed by this program");
            lock (_logLock)
            {
                try
                {
                    _log.Dispose();
                }
                catch (Exception)
                {
                }
            }
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Note(string text)
    {
        lock (_logLock)
        {
            try
            {
                _log.WriteLine(Stamp() + " # " + text);
            }
            catch (Exception)
            {
                // The file is gone or full: the connection goes on without a record.
            }
        }
    }

    private void Record(string direction, byte[] buffer, int offset, int count, bool mask)
    {
        lock (_logLock)
        {
            try
            {
                for (int start = 0; start < count; start += BytesPerLine)
                {
                    int length = Math.Min(BytesPerLine, count - start);
                    StringBuilder hex = new StringBuilder();
                    StringBuilder text = new StringBuilder();
                    for (int i = 0; i < length; i++)
                    {
                        byte b = buffer[offset + start + i];
                        bool printable = b >= 0x20 && b < 0x7F;
                        // The state has to follow every byte, printable or not.
                        bool command = mask && IsTelnetCommandByte(b, ref _txState);
                        bool hidden = mask && printable && !command;
                        hex.Append(hidden ? "**" : b.ToString("X2")).Append(' ');
                        text.Append(printable ? (hidden ? '*' : (char)b) : '.');
                    }
                    string head = start == 0
                        ? Stamp() + " " + direction + " " + count.ToString().PadLeft(4) + " bytes  "
                        : new string(' ', Stamp().Length + 1 + direction.Length + 1 + 4 + 8);
                    _log.WriteLine(head + hex.ToString().PadRight(BytesPerLine * 3) + " |" + text + "|");
                }
            }
            catch (Exception)
            {
                // The file is gone or full: the connection goes on without a record.
            }
        }
    }

    // Where a sent byte is in a telnet command: the command byte after IAC, the option after WILL/WONT/DO/DONT or SB.
    // Those are not the user's text, and seeing them (a printable option such as 0x20 or 0x27) is what the record is for.
    private enum TxState
    {
        Text,
        AfterIac,
        Option,
        InSubnegotiation,
        SubnegotiationOption
    }

    private TxState _txState;

    private static bool IsTelnetCommandByte(byte b, ref TxState state)
    {
        const byte IAC = 0xFF, SB = 0xFA, SE = 0xF0, WILL = 0xFB, DONT = 0xFE;
        switch (state)
        {
            case TxState.AfterIac:
                state = b >= WILL && b <= DONT ? TxState.Option : b == SB ? TxState.SubnegotiationOption : TxState.Text;
                return true;
            case TxState.Option:
                state = TxState.Text;
                return true;
            case TxState.SubnegotiationOption:
                state = TxState.InSubnegotiation;
                return true;
            case TxState.InSubnegotiation:
                // The content (a terminal type, a variable) stays masked; only the end is looked for.
                if (b == IAC)
                {
                    state = TxState.AfterIac;
                }
                return false;
            default:
                if (b == IAC)
                {
                    state = TxState.AfterIac;
                }
                return false;
        }
    }

    private string Stamp()
    {
        return "[" + (_clock.ElapsedMilliseconds / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture).PadLeft(8) + " s]";
    }
}
