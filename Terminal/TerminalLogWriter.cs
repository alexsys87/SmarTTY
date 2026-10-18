using System;
using System.IO;
using System.Text;

namespace SmarTTY.Terminal;

public enum TerminalLogFormat
{
    /// <summary>Received text, decoded with the terminal's encoding and stored as UTF-8 without a BOM.</summary>
    Text,

    /// <summary>Readable hex dump: offset, 16 bytes per line and an ASCII column (like <c>hexdump -C</c>).</summary>
    HexDump,

    /// <summary>The received bytes exactly as they arrived.</summary>
    RawBytes
}

/// <summary>
/// Continuously records data received from a COM/TCP device into a file. Each chunk is flushed as soon as it
/// is written, so the log is complete even if the application is killed, and other programs can read it while
/// recording is in progress. Instances are not thread-safe; call them from the thread that receives the data.
/// </summary>
/// <remarks>
/// The writer works on the raw byte stream, so it records the same data no matter whether the terminal is
/// currently showing text or hex. A failure (for example a full disk or a removed drive) stops the recording and
/// is reported through <see cref="Failed"/> instead of being silently ignored.
/// </remarks>
public sealed class TerminalLogWriter : IDisposable
{
    private const int BytesPerLine = 16;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly FileStream _stream;

    private TextStreamDecoder _decoder;

    private long _hexOffset;

    private bool _disposed;

    /// <param name="append">Append to an existing file instead of replacing it.</param>
    public TerminalLogWriter(string filePath, TerminalLogFormat format, Encoding textEncoding, bool append)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A log file name is required.", nameof(filePath));
        }
        FilePath = filePath;
        Format = format;
        _decoder = new TextStreamDecoder(textEncoding ?? Encoding.UTF8);
        _stream = new FileStream(filePath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public string FilePath { get; }

    public TerminalLogFormat Format { get; }

    /// <summary>Bytes this writer has put into the file so far (for a hex dump this is larger than the received data).</summary>
    public long BytesWritten { get; private set; }

    /// <summary>The error that stopped the recording, or null while it is healthy.</summary>
    public Exception Error { get; private set; }

    public bool IsActive => !_disposed && Error == null;

    /// <summary>Raised once, from <see cref="Write"/>, when the file can no longer be written. Recording has already stopped.</summary>
    public event Action<Exception> Failed;

    public void Write(byte[] buffer, int offset, int count)
    {
        if (!IsActive || count <= 0)
        {
            return;
        }
        try
        {
            switch (Format)
            {
                case TerminalLogFormat.RawBytes:
                    WriteToFile(buffer, offset, count);
                    break;
                case TerminalLogFormat.HexDump:
                    WriteToFile(Encoding.ASCII.GetBytes(FormatHexDump(buffer, offset, count)));
                    break;
                default:
                    string text = _decoder.Decode(buffer, offset, count);
                    if (text.Length > 0)
                    {
                        WriteToFile(Utf8NoBom.GetBytes(text));
                    }
                    break;
            }
            _stream.Flush();
        }
        catch (Exception exception)
        {
            Error = exception;
            CloseStream();
            Failed?.Invoke(exception);
        }
    }

    /// <summary>Follows an encoding change made while recording, so the text log matches what the terminal shows.</summary>
    public void SetEncoding(Encoding textEncoding)
    {
        _decoder = new TextStreamDecoder(textEncoding ?? Encoding.UTF8);
    }

    /// <summary>Drops a half-received character, e.g. after the device was reconnected.</summary>
    public void ResetDecoder()
    {
        _decoder.Reset();
    }

    public void Dispose()
    {
        _disposed = true;
        CloseStream();
    }

    private void WriteToFile(byte[] bytes)
    {
        WriteToFile(bytes, 0, bytes.Length);
    }

    private void WriteToFile(byte[] bytes, int offset, int count)
    {
        _stream.Write(bytes, offset, count);
        BytesWritten += count;
    }

    private void CloseStream()
    {
        try
        {
            _stream.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// One block per received chunk, so chunk boundaries stay visible and every line is complete when written.
    /// Offsets keep counting across chunks: <c>00000000  41 42 ...  |AB...|</c>.
    /// </summary>
    private string FormatHexDump(byte[] buffer, int offset, int count)
    {
        StringBuilder text = new StringBuilder();
        for (int lineStart = 0; lineStart < count; lineStart += BytesPerLine)
        {
            int lineLength = Math.Min(BytesPerLine, count - lineStart);
            text.Append((_hexOffset + lineStart).ToString("X8")).Append("  ");
            for (int i = 0; i < BytesPerLine; i++)
            {
                if (i == 8)
                {
                    text.Append(' ');
                }
                if (i < lineLength)
                {
                    text.Append(buffer[offset + lineStart + i].ToString("X2")).Append(' ');
                }
                else
                {
                    text.Append("   ");
                }
            }
            text.Append(" |");
            for (int i = 0; i < lineLength; i++)
            {
                byte value = buffer[offset + lineStart + i];
                text.Append(value >= 0x20 && value < 0x7F ? (char)value : '.');
            }
            text.Append("|\r\n");
        }
        _hexOffset += count;
        return text.ToString();
    }
}
