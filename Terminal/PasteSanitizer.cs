using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Terminal;

/// <summary>
/// Preparation of text that is pasted into a terminal, free of WinForms so that the Mono test harness can compile it:
/// removal of escape sequences and control characters (a page can put hidden "paste-jacking" commands or terminal
/// queries into the text that is copied from it), replacement of typographic characters that a shell does not
/// understand, line break conversion, and the detection of commands that are dangerous to run unseen.
/// All functions that look at line breaks expect text normalized with <see cref="NormalizeLineBreaks"/> (LF only).
/// </summary>
public static class PasteSanitizer
{
    private sealed class DangerPattern
    {
        public readonly string Description;

        public readonly Regex Expression;

        public DangerPattern(string description, string pattern)
        {
            Description = description;
            Expression = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
    }

    // A command word is not preceded by a name character, so that "pseudo" or "my-rm" do not match ("/bin/rm" does).
    private const string Before = @"(?<![\w.-])";

    private static readonly DangerPattern[] DangerPatterns =
    {
        new DangerPattern("runs a command as root (sudo)", Before + @"sudo(?![\w-])"),
        new DangerPattern("switches to root (su)", Before + @"su(\s+-\S*)?\s*$"),
        new DangerPattern("downloads a script and runs it at once (curl | sh)",
            @"\b(curl|wget|fetch)\b[^|\n]*\|\s*(sudo\s+(-\S+\s+)*)?(\S*/)?(ba|z|da|k|c|a)?sh\b"),
        new DangerPattern("downloads a script and runs it at once (sh <(curl ...))",
            @"\b(ba|z|da|k|c|a)?sh\s+<\(\s*(curl|wget)\b|\beval\s+[""']?\$\(\s*(curl|wget)\b"),
        new DangerPattern("recursive or forced delete (rm -rf)",
            Before + @"rm\s+(?:\S+\s+)*?(?:-[a-zA-Z]*[rRf][a-zA-Z]*|--recursive|--force|--no-preserve-root)(?=\s|$)"),
        new DangerPattern("writes straight to a disk device (dd, > /dev/sdX)",
            @"\bdd\b[^\n]*\bof=/dev/|>\s*/dev/(sd|hd|vd|nvme|mmcblk|mtdblock)"),
        new DangerPattern("formats or wipes a disk (mkfs, wipefs, shred)", Before + @"(mkfs(\.\w+)?|wipefs|shred)(?![\w-])"),
        new DangerPattern("fork bomb", @":\s*\(\s*\)\s*\{[^}]*:\s*\|\s*:"),
        new DangerPattern("changes permissions or owner of a whole tree (chmod/chown -R /)",
            Before + @"(chmod|chown)\s+(?:-\S+\s+)*-[a-zA-Z]*R\S*\s+(?:\S+\s+)?/(\s|$)"),
        new DangerPattern("opens permissions to everyone (chmod 777)", Before + @"chmod\b[^\n]*\b0?777\b"),
        new DangerPattern("shuts down or reboots the machine", Before + @"(shutdown|reboot|poweroff|halt|init\s+[06])(?![\w-])"),
        new DangerPattern("writes firmware or resets the device (sysupgrade, mtd, firstboot)",
            Before + @"(sysupgrade|firstboot|jffs2reset|flash_erase|mtd\s+(-\S+\s+)*(write|erase))(?![\w-])")
    };

    public const string ControlCharactersWarning = "contains escape sequences or control characters";

    /// <summary>CR LF and a lone CR become LF.</summary>
    public static string NormalizeLineBreaks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>
    /// Removes escape sequences (CSI, OSC, DCS and the like, which includes the bracketed paste markers) and control
    /// characters other than tab and line feed, the C1 controls, DEL and invisible formatting characters
    /// (zero-width characters and the bidirectional overrides that make a command look different from what runs).
    /// </summary>
    public static string RemoveControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        StringBuilder result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\u001b')
            {
                i = EndOfEscapeSequence(text, i);
                continue;
            }
            if (ch == '\u009b')
            {
                // The 8-bit form of CSI.
                i = EndOfCsi(text, i + 1);
                continue;
            }
            if (ch == '\n' || ch == '\t')
            {
                result.Append(ch);
                continue;
            }
            if (IsControlCharacter(ch) || IsInvisibleFormatting(ch))
            {
                continue;
            }
            result.Append(ch);
        }
        return result.ToString();
    }

    /// <summary>True when the text has something <see cref="RemoveControlCharacters"/> would remove.</summary>
    public static bool HasControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        foreach (char ch in text)
        {
            if (ch != '\n' && ch != '\t' && (IsControlCharacter(ch) || IsInvisibleFormatting(ch)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsControlCharacter(char ch)
    {
        return ch < ' ' || (ch >= '\u007f' && ch <= '\u009f');
    }

    private static bool IsInvisibleFormatting(char ch)
    {
        return (ch >= '​' && ch <= '‏') || (ch >= '‪' && ch <= '‮') ||
            (ch >= '⁠' && ch <= '⁤') || (ch >= '⁦' && ch <= '⁩') || ch == '﻿';
    }

    // Index of the last character of the CSI sequence whose parameters start at <start> (the character after "ESC [").
    // A sequence that is cut short ends before the character that does not belong to it.
    private static int EndOfCsi(string text, int start)
    {
        int j = start;
        while (j < text.Length && text[j] >= '0' && text[j] <= '?')
        {
            j++;
        }
        while (j < text.Length && text[j] >= ' ' && text[j] <= '/')
        {
            j++;
        }
        if (j < text.Length && text[j] >= '@' && text[j] <= '~')
        {
            return j;
        }
        return j - 1;
    }

    // Index of the last character of the escape sequence that starts with the ESC at <start>.
    private static int EndOfEscapeSequence(string text, int start)
    {
        if (start + 1 >= text.Length)
        {
            return start;
        }
        char kind = text[start + 1];
        if (kind == '[')
        {
            return EndOfCsi(text, start + 2);
        }
        if (kind == ']' || kind == 'P' || kind == 'X' || kind == '^' || kind == '_')
        {
            // A string (window title, DCS, ...) up to BEL or ST. A line break ends it as well, so that a sequence that is
            // never closed does not swallow the rest of the pasted text.
            for (int j = start + 2; j < text.Length; j++)
            {
                if (text[j] == '\u0007')
                {
                    return j;
                }
                if (text[j] == '\u001b' && j + 1 < text.Length && text[j + 1] == '\\')
                {
                    return j + 1;
                }
                if (text[j] == '\n')
                {
                    return j - 1;
                }
            }
            return text.Length - 1;
        }
        int k = start + 1;
        while (k < text.Length && text[k] >= ' ' && text[k] <= '/')
        {
            k++;
        }
        if (k < text.Length && text[k] >= '0' && text[k] <= '~')
        {
            return k;
        }
        return k - 1;
    }

    /// <summary>
    /// Replaces what word processors and web pages put in place of ASCII with the ASCII characters: curly quotes,
    /// en and em dashes (an em dash in "—version" is how "--version" arrives), the minus sign, no-break and other
    /// special spaces, and the ellipsis.
    /// </summary>
    public static string ReplaceTypographicCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        StringBuilder result = null;
        for (int i = 0; i < text.Length; i++)
        {
            string replacement = Replacement(text[i]);
            if (replacement == null)
            {
                result?.Append(text[i]);
                continue;
            }
            if (result == null)
            {
                result = new StringBuilder(text.Length + 8);
                result.Append(text, 0, i);
            }
            result.Append(replacement);
        }
        return result == null ? text : result.ToString();
    }

    private static string Replacement(char ch)
    {
        switch (ch)
        {
            case '‘':
            case '’':
            case '‚':
            case '‛':
            case '′':
            case 'ʼ':
                return "'";
            case '“':
            case '”':
            case '„':
            case '‟':
            case '″':
                return "\"";
            case '‐':
            case '‑':
            case '‒':
            case '–':
            case '−':
                return "-";
            case '—':
            case '―':
                return "--";
            case '…':
                return "...";
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case '　':
                return " ";
            default:
                return (ch >= ' ' && ch <= ' ') ? " " : null;
        }
    }

    /// <summary>Removes the line breaks at the end, so that the last pasted line is not run before the user presses Enter.</summary>
    public static string TrimTrailingLineBreaks(string text)
    {
        return string.IsNullOrEmpty(text) ? (text ?? "") : text.TrimEnd('\n');
    }

    /// <summary>Turns the LF line breaks of normalized text into <paramref name="lineBreak"/> ("\r", "\n" or "\r\n").</summary>
    public static string ConvertLineBreaks(string text, string lineBreak)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(lineBreak) || lineBreak == "\n")
        {
            return text ?? "";
        }
        return text.Replace("\n", lineBreak);
    }

    /// <summary>The number of line breaks in normalized text.</summary>
    public static int CountLineBreaks(string text)
    {
        int count = 0;
        if (text != null)
        {
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>The number of lines of normalized text: a final line break does not start another line.</summary>
    public static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        return CountLineBreaks(text) + (text[text.Length - 1] == '\n' ? 0 : 1);
    }

    /// <summary>
    /// What is dangerous in text that would run at once (a description of each kind of danger, once): sudo, a script
    /// downloaded and piped into a shell, rm -rf, writes to disks, shutdown, firmware writes and control characters.
    /// </summary>
    public static List<string> FindDangerous(string text)
    {
        List<string> found = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return found;
        }
        if (HasControlCharacters(text))
        {
            found.Add(ControlCharactersWarning);
        }
        foreach (DangerPattern pattern in DangerPatterns)
        {
            foreach (string line in text.Split('\n'))
            {
                bool matched;
                try
                {
                    matched = pattern.Expression.IsMatch(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    matched = true;
                }
                if (matched)
                {
                    found.Add(pattern.Description);
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>
    /// The text for the preview of a confirmation: at most <paramref name="maxLines"/> lines, long lines cut, control
    /// characters shown as their symbols (ESC as a visible character) instead of acting on the control.
    /// </summary>
    public static string BuildPreview(string text, int maxLines, int maxLineLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        string[] lines = text.Split('\n');
        int shown = Math.Min(lines.Length, Math.Max(maxLines, 1));
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < shown; i++)
        {
            string line = lines[i];
            if (line.Length > maxLineLength)
            {
                line = line.Substring(0, maxLineLength) + "…";
            }
            foreach (char ch in line)
            {
                if (ch < ' ' && ch != '\t')
                {
                    result.Append((char)('␀' + ch));
                }
                else if (ch == '\u007f' || (ch >= '\u0080' && ch <= '\u009f'))
                {
                    result.Append('␡');
                }
                else
                {
                    result.Append(ch);
                }
            }
            if (i < shown - 1)
            {
                result.Append("\r\n");
            }
        }
        if (shown < lines.Length)
        {
            result.Append("\r\n… and " + (lines.Length - shown) + " more line(s)");
        }
        return result.ToString();
    }
}
