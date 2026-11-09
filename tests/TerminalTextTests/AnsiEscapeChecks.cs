using System;
using System.Collections.Generic;
using SmarTTY.Terminal;

// Where an escape sequence ends when the stream is damaged (a serial line that lost characters, an output that was cut off).
// A CSI sequence that is cut short by ESC or by a control character used to take the next sequence, or the control
// character and the letter after it, along with it: "ESC[31" and "ESC[0m" made one sequence that ended with the '[' of the
// second one and left "0m" on the screen as text; a CR, LF or BEL inside a sequence was never carried out.
internal static class AnsiEscapeChecks
{
    private const string Esc = "\u001b";

    private static string Scan(string text, int start = 0)
    {
        CsiScanResult result = AnsiEscapeUtils.ScanCsi(text, start, out int index);
        return result + "@" + index;
    }

    public static void Run()
    {
        // ---- the ordinary sequences are found where they always were
        Program.Equal(Scan("31m"), "Final@2", "CSI: SGR ends at its final byte");
        Program.Equal(Scan("1;31;42m"), "Final@7", "CSI: parameters separated by semicolons");
        Program.Equal(Scan("m"), "Final@0", "CSI: no parameters");
        Program.Equal(Scan("?25l"), "Final@3", "CSI: a private mode");
        Program.Equal(Scan("2 q"), "Final@2", "CSI: an intermediate byte before the final one");
        Program.Equal(Scan("3~"), "Final@1", "CSI: the final byte '~' (function keys)");
        Program.Equal(Scan("5@"), "Final@1", "CSI: the final byte '@' (insert characters)");
        Program.Equal(Scan("[A"), "Final@0", "CSI: '[' is a final byte (ESC[[A of the Linux console), as before");
        Program.Equal(Scan("xx31m", 2), "Final@4", "CSI: the scan starts where it is told to");
        Program.Equal(Scan("31m\r"), "Final@2", "CSI: what comes after the final byte does not matter");
        Program.Equal(Scan("3\u007f1m"), "Final@3", "CSI: DEL stays in the body, as before");
        Program.Equal(Scan("3\u00e91m"), "Final@3", "CSI: so does a letter above 0x7E, as before");

        // ---- not complete yet: the caller keeps it for the next chunk of output
        Program.Equal(Scan(""), "Incomplete@0", "CSI: nothing after ESC [ yet");
        Program.Equal(Scan("31"), "Incomplete@2", "CSI: parameters without a final byte");
        Program.Equal(Scan("1;"), "Incomplete@2", "CSI: parameters that stop at a semicolon");
        Program.Equal(Scan("?"), "Incomplete@1", "CSI: a private marker only");
        Program.Equal(Scan("2 "), "Incomplete@2", "CSI: an intermediate byte only");
        Program.Equal(Scan("xx31", 2), "Incomplete@4", "CSI: incomplete after a start index");

        // ---- cut short by ESC: it is the start of the next sequence and is not taken
        Program.Equal(Scan("31" + Esc + "[0m"), "Cancelled@2", "ESC[31 then ESC[0m: the first is dropped and the second begins at its ESC");
        Program.Equal(Scan(Esc + "[0m"), "Cancelled@0", "ESC[ then ESC[0m: nothing was in the first");
        Program.Equal(Scan("1;" + Esc + "]0;t\a"), "Cancelled@2", "ESC[1; then an OSC");
        Program.Equal(Scan("31" + Esc), "Cancelled@2", "ESC[31 then ESC at the end of the chunk: the ESC is the beginning of what comes next");

        // ---- cut short by a control character: it is not part of the sequence and is carried out by the caller
        foreach (char control in new[] { '\r', '\n', '\a', '\t', '\b', '\0', '\u000e', '\u000f', '\u0001', '\u001f' })
        {
            Program.Equal(Scan("31" + control + "text"), "Cancelled@2", "a control character 0x" + ((int)control).ToString("x2") + " in the middle of a CSI cancels it and is left for the caller");
        }
        Program.Equal(Scan("\rm"), "Cancelled@0", "ESC[ then CR: the 'm' after it is text, not the final byte");

        // ---- CAN and SUB cancel the sequence and are consumed with it (as in xterm and as for the OSC strings)
        Program.Equal(Scan("31\u0018text"), "Cancelled@3", "CAN cancels the sequence and is part of it");
        Program.Equal(Scan("31\u001atext"), "Cancelled@3", "SUB as well");
        Program.Equal(Scan("\u0018"), "Cancelled@1", "CAN right after ESC [");

        // ---- which characters interrupt an escape sequence
        bool borders = true;
        for (char ch = '\0'; ch < ' '; ch++)
        {
            borders &= AnsiEscapeUtils.InterruptsEscape(ch);
        }
        Program.Check(borders, "ESC and all the C0 control characters interrupt an escape sequence");
        Program.Check(!AnsiEscapeUtils.InterruptsEscape(' ') && !AnsiEscapeUtils.InterruptsEscape('[') && !AnsiEscapeUtils.InterruptsEscape('(') && !AnsiEscapeUtils.InterruptsEscape('~') && !AnsiEscapeUtils.InterruptsEscape('\u007f') && !AnsiEscapeUtils.InterruptsEscape('\u00e9'), "a space, the characters of sequences, DEL and letters do not");
        Program.Check(AnsiEscapeUtils.InterruptsEscape(Esc[0]), "ESC does");

        // ---- every short stream: the answer is always inside the text, and the caller always makes progress
        char[] alphabet = { '1', ';', 'm', '[', ' ', '\u001b', '\r', '\u0018' };
        int streams = 0;
        bool sane = true;
        List<string> bad = new List<string>();
        void Visit(string prefix, int length)
        {
            for (int start = 0; start <= prefix.Length; start++)
            {
                streams++;
                CsiScanResult result = AnsiEscapeUtils.ScanCsi(prefix, start, out int index);
                bool ok = index >= start && index <= prefix.Length;
                if (result == CsiScanResult.Final)
                {
                    ok &= index < prefix.Length && prefix[index] >= '@' && prefix[index] <= '~';
                    for (int i = start; i < index; i++)
                    {
                        ok &= !(prefix[i] >= '@' && prefix[i] <= '~') && prefix[i] >= ' ';
                    }
                }
                else if (result == CsiScanResult.Incomplete)
                {
                    ok &= index == prefix.Length;
                    for (int i = start; i < prefix.Length; i++)
                    {
                        ok &= prefix[i] >= ' ' && !(prefix[i] >= '@' && prefix[i] <= '~');
                    }
                }
                else
                {
                    // Cancelled: what is dropped holds no final byte, and what stops it is a control character.
                    ok &= index > start || (index == start && prefix.Length > start && AnsiEscapeUtils.InterruptsEscape(prefix[start]));
                }
                if (!ok && bad.Count < 5)
                {
                    bad.Add(prefix.Replace("\u001b", "<ESC>").Replace("\r", "<CR>").Replace("\u0018", "<CAN>") + " from " + start + ": " + result + "@" + index);
                }
                sane &= ok;
            }
            if (length == 0)
            {
                return;
            }
            foreach (char ch in alphabet)
            {
                Visit(prefix + ch, length - 1);
            }
        }
        Visit("", 5);
        Program.Check(sane, "every stream of up to 5 characters of a CSI alphabet, from every start (" + streams + " scans): the result is consistent" + (bad.Count == 0 ? "" : " (e.g. " + string.Join("; ", bad) + ")"));
    }
}
