namespace SmarTTY.Terminal;

/// <summary>How the scan of the rest of a CSI sequence ended (see <see cref="AnsiEscapeUtils.ScanCsi"/>).</summary>
public enum CsiScanResult
{
    /// <summary>The final byte was found.</summary>
    Final,

    /// <summary>The text ended first: the rest may follow in the next chunk of output.</summary>
    Incomplete,

    /// <summary>Something that cannot belong to the sequence came first: the sequence is dropped.</summary>
    Cancelled
}

public static class AnsiEscapeUtils
{
    /// <summary>
    /// True for ESC and the other C0 control characters (CR, LF, BEL, ...): none of them can be a part of an escape sequence.
    /// When one comes in the middle of a sequence the stream is damaged (a serial line that lost characters, an output that
    /// was cut), and the sequence it interrupts has to give way instead of swallowing it and what follows it.
    /// </summary>
    public static bool InterruptsEscape(char ch)
    {
        return ch < ' ';
    }

    /// <summary>
    /// Scans the rest of a CSI sequence (<paramref name="startIndex"/> is the character after "ESC [") for its final byte.
    /// The parameters, a private marker and the intermediate bytes are 0x20..0x3F, the final byte is 0x40..0x7E.
    /// ESC and the control characters cut the sequence short, so that a sequence that was cut off by a lost character
    /// ("ESC[31" and then "ESC[0m") does not take the next one with it, and a CR, LF or BEL in the middle of it is still
    /// carried out by the caller. CAN and SUB cancel the sequence and belong to it, as in xterm. (xterm carries out the
    /// control characters and goes on with the sequence; with a damaged stream the rest of it is as likely to be text.)
    /// <paramref name="index"/> is the position of the final byte for <see cref="CsiScanResult.Final"/>; for
    /// <see cref="CsiScanResult.Cancelled"/> it is the position after the part of the text that is dropped.
    /// </summary>
    public static CsiScanResult ScanCsi(string text, int startIndex, out int index)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch >= '@' && ch <= '~')
            {
                index = i;
                return CsiScanResult.Final;
            }
            if (ch == '\u0018' || ch == '\u001a')
            {
                index = i + 1;
                return CsiScanResult.Cancelled;
            }
            if (InterruptsEscape(ch))
            {
                index = i;
                return CsiScanResult.Cancelled;
            }
        }
        index = text.Length;
        return CsiScanResult.Incomplete;
    }
}
