using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Terminal;

// Regression harness for the preparation of pasted text (Terminal/PasteSanitizer.cs): removal of escape sequences and
// control characters, typographic replacements, line breaks, line counting, detection of dangerous commands and the
// preview of the confirmation.
// Run with: dotnet run --project tests/PasteSanitizerTests
internal static class Program
{
    private static int _passed;

    private const string Esc = "\u001b";

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static string Show(string text)
    {
        return text?.Replace(Esc, "ESC").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static void Equal(string actual, string expected, string name)
    {
        Check(actual == expected, name + (actual == expected ? "" : $" (expected '{Show(expected)}', got '{Show(actual)}')"));
    }

    private static bool Dangerous(string text, string part)
    {
        return PasteSanitizer.FindDangerous(text).Any(item => item.Contains(part));
    }

    private static void LineBreakChecks()
    {
        Equal(PasteSanitizer.NormalizeLineBreaks("a\r\nb\rc\nd"), "a\nb\nc\nd", "CR LF, CR and LF all become LF");
        Equal(PasteSanitizer.NormalizeLineBreaks(null), "", "null text");
        Equal(PasteSanitizer.ConvertLineBreaks("a\nb\n", "\r"), "a\rb\r", "LF to CR");
        Equal(PasteSanitizer.ConvertLineBreaks("a\nb", "\r\n"), "a\r\nb", "LF to CR LF");
        Equal(PasteSanitizer.ConvertLineBreaks("a\nb", "\n"), "a\nb", "LF stays");
        Equal(PasteSanitizer.TrimTrailingLineBreaks("ls\n\n"), "ls", "trailing line breaks are removed");
        Equal(PasteSanitizer.TrimTrailingLineBreaks("a\nb"), "a\nb", "inner line breaks stay");
        Check(PasteSanitizer.CountLineBreaks("a\nb\n") == 2, "line breaks are counted");
        Check(PasteSanitizer.CountLines("a\nb\n") == 2, "a final line break does not start a line");
        Check(PasteSanitizer.CountLines("a\nb") == 2, "two lines without a final line break");
        Check(PasteSanitizer.CountLines("ls") == 1, "one line");
        Check(PasteSanitizer.CountLines("") == 0, "no lines");
    }

    private static void ControlChecks()
    {
        Equal(PasteSanitizer.RemoveControlCharacters("ls -l"), "ls -l", "plain text stays");
        Equal(PasteSanitizer.RemoveControlCharacters("a\tb\nc"), "a\tb\nc", "tab and line feed stay");
        Equal(PasteSanitizer.RemoveControlCharacters("a" + Esc + "[31mred" + Esc + "[0m"), "ared", "a colour sequence is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("a" + Esc + "[200~b" + Esc + "[201~c"), "abc", "bracketed paste markers are removed");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "]0;title\u0007y"), "xy", "an OSC string ended with BEL is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "]0;title" + Esc + "\\y"), "xy", "an OSC string ended with ST is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "]0;never closed\ny"), "x\ny", "an OSC string that is not closed stops at the line break");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "Py" + Esc + "\\z"), "xz", "a DCS string is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "(Bz"), "xz", "a character set sequence is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "7y"), "xy", "a two-character escape is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc), "x", "a lone ESC at the end is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "\ny"), "x\ny", "ESC before a line break leaves the line break");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "[31"), "x", "a cut CSI sequence is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("x" + Esc + "[1;2\ny"), "x\ny", "a cut CSI sequence stops at the line break");
        Equal(PasteSanitizer.RemoveControlCharacters("a\u0007b\u0008c\u0003d\u007fe"), "abcde", "BEL, BS, Ctrl+C and DEL are removed");
        Equal(PasteSanitizer.RemoveControlCharacters("a\u009b31mb"), "ab", "the 8-bit CSI is removed");
        Equal(PasteSanitizer.RemoveControlCharacters("a\u0085b\u009fc"), "abc", "C1 controls are removed");
        Equal(PasteSanitizer.RemoveControlCharacters("a\u200bb\u202ec\u2066d\ufeffe"), "abcde", "zero-width and bidirectional characters are removed");
        Equal(PasteSanitizer.RemoveControlCharacters("\u041f\u0440\u0438\u0432\u0435\u0442 \u4e16\u754c"), "\u041f\u0440\u0438\u0432\u0435\u0442 \u4e16\u754c", "letters of other scripts stay");
        Check(PasteSanitizer.HasControlCharacters("a" + Esc + "[0mb"), "escape sequences are detected");
        Check(PasteSanitizer.HasControlCharacters("a\u202eb"), "a bidirectional override is detected");
        Check(!PasteSanitizer.HasControlCharacters("a\tb\nc"), "tab and line feed are not control characters here");
    }

    private static void TypographicChecks()
    {
        Equal(PasteSanitizer.ReplaceTypographicCharacters("echo \u201chi\u201d \u2018x\u2019"), "echo \"hi\" 'x'", "curly quotes");
        Equal(PasteSanitizer.ReplaceTypographicCharacters("ls \u2014all \u2013l"), "ls --all -l", "em and en dashes");
        Equal(PasteSanitizer.ReplaceTypographicCharacters("a\u00a0b\u2009c\u202fd"), "a b c d", "special spaces");
        Equal(PasteSanitizer.ReplaceTypographicCharacters("wait\u2026"), "wait...", "ellipsis");
        Equal(PasteSanitizer.ReplaceTypographicCharacters("-5 \u22122"), "-5 -2", "minus sign");
        Equal(PasteSanitizer.ReplaceTypographicCharacters("plain 'text' \"here\""), "plain 'text' \"here\"", "ASCII is left alone");
        Equal(PasteSanitizer.ReplaceTypographicCharacters(""), "", "empty text");
    }

    private static void DangerChecks()
    {
        Check(Dangerous("sudo apt update\n", "sudo"), "sudo");
        Check(Dangerous("ls && /usr/bin/sudo reboot", "sudo"), "sudo with a path");
        Check(!Dangerous("echo pseudo\n", "sudo"), "pseudo is not sudo");
        Check(!Dangerous("cat sudoers.txt\n", "sudo"), "a file name with sudo in it");
        Check(Dangerous("curl -fsSL https://x.example/i.sh | sh\n", "curl"), "curl piped into sh");
        Check(Dangerous("wget -qO- https://x.example/i.sh | sudo bash\n", "curl"), "wget piped into sudo bash");
        Check(Dangerous("bash <(curl -s https://x.example/i.sh)\n", "curl"), "bash <(curl ...)");
        Check(Dangerous("eval \"$(curl -s https://x.example/i.sh)\"\n", "curl"), "eval of curl output");
        Check(!Dangerous("curl -o file.txt https://x.example/f\n", "curl"), "a plain download is fine");
        Check(Dangerous("rm -rf /tmp/x\n", "rm -rf"), "rm -rf");
        Check(Dangerous("rm -fr x\n", "rm -rf"), "rm -fr");
        Check(Dangerous("/bin/rm -r x\n", "rm -rf"), "rm -r with a path");
        Check(Dangerous("rm x -f\n", "rm -rf"), "rm with the option after the file");
        Check(Dangerous("rm --recursive dir\n", "rm -rf"), "rm --recursive");
        Check(!Dangerous("rm file.txt\n", "rm -rf"), "a plain rm is fine");
        Check(!Dangerous("echo confirm -rf\n", "rm -rf"), "confirm is not rm");
        Check(Dangerous("dd if=/dev/zero of=/dev/sda\n", "disk"), "dd to a device");
        Check(Dangerous("cat x > /dev/sdb\n", "disk"), "redirect to a device");
        Check(!Dangerous("dd if=/dev/zero of=file bs=1M count=1\n", "disk"), "dd to a file is fine");
        Check(Dangerous("mkfs.ext4 /dev/sda1\n", "mkfs"), "mkfs");
        Check(Dangerous(":(){ :|:& };:\n", "fork bomb"), "fork bomb");
        Check(Dangerous("chmod -R 755 /\n", "chmod"), "chmod -R on /");
        Check(Dangerous("chmod 777 /etc/passwd\n", "777"), "chmod 777");
        Check(!Dangerous("chmod 644 file\n", "777"), "chmod 644 is fine");
        Check(Dangerous("shutdown -h now\n", "shuts down"), "shutdown");
        Check(Dangerous("reboot\n", "shuts down"), "reboot");
        Check(Dangerous("sysupgrade -n /tmp/fw.bin\n", "firmware"), "sysupgrade");
        Check(Dangerous("mtd write fw.bin firmware\n", "firmware"), "mtd write");
        Check(Dangerous("su -\n", "root"), "su -");
        Check(!Dangerous("echo summary\n", "root"), "summary is not su");
        Check(Dangerous("ls" + Esc + "[2Jx\n", "control"), "control characters");
        Check(PasteSanitizer.FindDangerous("ls -l\npwd\n").Count == 0, "harmless commands");
        Check(PasteSanitizer.FindDangerous("sudo a\nsudo b\n").Count == 1, "a kind of danger is reported once");
        Check(PasteSanitizer.FindDangerous("").Count == 0, "empty text");
    }

    private static void PreviewChecks()
    {
        Equal(PasteSanitizer.BuildPreview("a\nb", 10, 10), "a\r\nb", "short text is shown whole");
        Equal(PasteSanitizer.BuildPreview("a\nb\nc\nd", 2, 10), "a\r\nb\r\n\u2026 and 2 more line(s)", "long text is cut");
        Equal(PasteSanitizer.BuildPreview("abcdefghij", 5, 4), "abcd\u2026", "a long line is cut");
        Equal(PasteSanitizer.BuildPreview("a" + Esc + "b", 5, 10), "a\u241bb", "ESC is shown as a symbol");
        Equal(PasteSanitizer.BuildPreview("", 5, 10), "", "empty text");
    }

    private static void Main()
    {
        LineBreakChecks();
        ControlChecks();
        TypographicChecks();
        DangerChecks();
        PreviewChecks();
        Console.WriteLine($"All {_passed} checks passed.");
    }
}
