using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.IO;

public enum UBootStepKind
{
    /// <summary>Sets up the network variables the download needs.</summary>
    Setup,

    /// <summary>The TFTP download itself.</summary>
    Download,

    /// <summary>What the user wants done with the downloaded image (boot it, write it to flash, ...).</summary>
    After
}

public sealed class UBootStep
{
    public UBootStep(UBootStepKind kind, string command)
    {
        Kind = kind;
        Command = command;
    }

    public UBootStepKind Kind { get; }

    public string Command { get; }
}

/// <summary>What the user fills in to get a firmware to a board that is waiting at the U-Boot prompt.</summary>
public sealed class UBootFirmwareSettings
{
    /// <summary>The address of this computer as the board sees it (the TFTP server).</summary>
    public string ServerIp { get; set; }

    /// <summary>The address the board should use; empty leaves the one stored in its environment.</summary>
    public string DeviceIp { get; set; }

    /// <summary>Where in the board's memory the file goes: a number (0x80060000) or an environment variable (${loadaddr}).</summary>
    public string LoadAddress { get; set; }

    /// <summary>The name the file is served under.</summary>
    public string FileName { get; set; }

    /// <summary>Commands to run once the download has succeeded, one per line.</summary>
    public string AfterCommands { get; set; }
}

/// <summary>
/// Turns the settings into the U-Boot commands that fetch the file, and checks them first: a value that does not belong on a
/// command line (a space in a file name, a stray ";") would run something else on the board.
/// </summary>
public static class UBootFirmwareScript
{
    /// <summary>The longest command line U-Boot is sure to take: the usual console buffer is 256 bytes with the end mark.</summary>
    public const int MaxCommandLength = 250;

    private static readonly Regex NameRegex = new Regex(@"^[A-Za-z0-9._+@,/\-]{1,200}$", RegexOptions.Compiled);
    private static readonly Regex AddressRegex = new Regex(@"^((0[xX])?[0-9A-Fa-f]{1,16}|\$\{[A-Za-z_][A-Za-z0-9_]*\})$", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new Regex(@"(?<!\$)\{(file|addr|serverip|ipaddr)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnfilledRegex = new Regex(@"<[A-Za-z][^<>]*>", RegexOptions.Compiled);

    public static bool IsValidIPv4(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string[] parts = text.Trim().Split('.');
        if (parts.Length != 4)
        {
            return false;
        }
        foreach (string part in parts)
        {
            // A leading zero is refused: some parsers read "010" as octal, and U-Boot would not agree with the user.
            if (part.Length == 0 || part.Length > 3 || (part.Length > 1 && part[0] == '0') || !int.TryParse(part, out int value) || value < 0 || value > 255)
            {
                return false;
            }
            foreach (char c in part)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }
        }
        return IPAddress.TryParse(text.Trim(), out _);
    }

    public static bool IsValidFileName(string name)
    {
        return name != null && NameRegex.IsMatch(name) && !name.StartsWith("-", StringComparison.Ordinal);
    }

    public static bool IsValidLoadAddress(string address)
    {
        return address != null && AddressRegex.IsMatch(address.Trim());
    }

    /// <summary>Returns the "&lt;something&gt;" left in a command for the user to fill in, or null.</summary>
    public static string FindUnfilledPlaceholder(string command)
    {
        if (command == null)
        {
            return null;
        }
        Match match = UnfilledRegex.Match(command);
        return match.Success ? match.Value : null;
    }

    /// <summary>The first problem with the settings in words for the user, or null when they are fine.</summary>
    public static string Validate(UBootFirmwareSettings settings)
    {
        if (settings == null)
        {
            return "No settings.";
        }
        if (!IsValidIPv4(settings.ServerIp))
        {
            return "The server address (this computer) is not a valid IPv4 address.";
        }
        if (!string.IsNullOrWhiteSpace(settings.DeviceIp) && !IsValidIPv4(settings.DeviceIp))
        {
            return "The device address is not a valid IPv4 address (leave it empty to keep the one the board already has).";
        }
        if (!IsValidLoadAddress(settings.LoadAddress))
        {
            return "The load address must be a hexadecimal number such as 0x80060000, or an environment variable such as ${loadaddr}.";
        }
        if (!IsValidFileName(settings.FileName))
        {
            return "The file name sent to the board may only contain letters, digits and . _ - + @ , / (no spaces) and must not start with a dash.";
        }

        List<string> after = SplitCommands(settings.AfterCommands);
        for (int i = 0; i < after.Count; i++)
        {
            string line = after[i];
            string expanded = Expand(line, settings);
            if (i < after.Count - 1 && NeverReturns(expanded))
            {
                return "\"" + Shorten(line) + "\" starts the board and U-Boot does not come back, so it has to be the last command.";
            }
            string unfilled = FindUnfilledPlaceholder(expanded);
            if (unfilled != null)
            {
                return "The command \"" + line + "\" still has " + unfilled + " to fill in.";
            }
            if (expanded.Length > MaxCommandLength)
            {
                return "The command \"" + Shorten(line) + "\" is longer than " + MaxCommandLength + " characters; U-Boot would cut it.";
            }
            foreach (char c in expanded)
            {
                if (c < ' ' && c != '\t')
                {
                    return "The command \"" + Shorten(line) + "\" contains a control character.";
                }
            }
        }
        return null;
    }

    /// <summary>The commands to send, in order. The settings must be valid (see <see cref="Validate"/>).</summary>
    public static List<UBootStep> Build(UBootFirmwareSettings settings)
    {
        List<UBootStep> steps = new List<UBootStep>();
        if (!string.IsNullOrWhiteSpace(settings.DeviceIp))
        {
            steps.Add(new UBootStep(UBootStepKind.Setup, "setenv ipaddr " + settings.DeviceIp.Trim()));
        }
        steps.Add(new UBootStep(UBootStepKind.Setup, "setenv serverip " + settings.ServerIp.Trim()));
        steps.Add(new UBootStep(UBootStepKind.Download, "tftpboot " + settings.LoadAddress.Trim() + " " + settings.FileName));
        foreach (string line in SplitCommands(settings.AfterCommands))
        {
            steps.Add(new UBootStep(UBootStepKind.After, Expand(line, settings)));
        }
        return steps;
    }

    /// <summary>The non-empty lines of a command list; a line starting with "#" is a comment and is dropped.</summary>
    public static List<string> SplitCommands(string text)
    {
        List<string> lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }
        foreach (string raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            string line = raw.Trim();
            if (line.Length > 0 && line[0] != '#')
            {
                lines.Add(line);
            }
        }
        return lines;
    }

    /// <summary>Replaces {file}, {addr}, {serverip} and {ipaddr} (the board's own ${ipaddr} when no device address is given).</summary>
    public static string Expand(string command, UBootFirmwareSettings settings)
    {
        return PlaceholderRegex.Replace(command, match =>
        {
            switch (match.Groups[1].Value.ToLowerInvariant())
            {
                case "file":
                    return settings.FileName ?? string.Empty;
                case "addr":
                    return (settings.LoadAddress ?? string.Empty).Trim();
                case "serverip":
                    return (settings.ServerIp ?? string.Empty).Trim();
                default:
                    return string.IsNullOrWhiteSpace(settings.DeviceIp) ? "${ipaddr}" : settings.DeviceIp.Trim();
            }
        });
    }

    /// <summary>True when the command can change what is stored on the board, so the user is asked before it is sent.</summary>
    public static bool IsDestructive(string command)
    {
        if (command == null)
        {
            return false;
        }
        string c = command.ToLowerInvariant();
        foreach (string word in new[] { "erase", "write", "fatwrite", "ext4write", "saveenv", "flash", "mmc write", "nand", "sf update", "env save", "dd ", "protect off" })
        {
            if (c.Contains(word))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly HashSet<string> StartingCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bootm", "bootz", "booti", "bootelf", "bootvx", "boot", "bootd", "go", "reset", "poweroff", "bootefi"
    };

    /// <summary>
    /// True when the command hands the board over to what it starts (an operating system, a program) or restarts it, so U-Boot's
    /// prompt does not come back and waiting for it would only run into the timeout.
    /// </summary>
    public static bool NeverReturns(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        foreach (string part in command.Split(';'))
        {
            string[] words = part.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && StartingCommands.Contains(words[0]))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] CommandProblems =
    {
        "Unknown command",
        "Usage:",
        "*** ERROR",
        "ERROR:",
        "Error:",
        "error -",
        "failed",
        "Bad ",
        "Wrong ",
        "Invalid",
        "not found",
        "can't ",
        "cannot ",
        "Unable to",
        "No SPI flash",
        "No such",
        "not supported",
        "timed out"
    };

    /// <summary>
    /// The line of a command's output that looks like a failure, or null. Used to stop before the next command: writing to
    /// flash after its erase failed would leave the board with a half-written image.
    /// </summary>
    public static string FindCommandProblem(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }
        foreach (string marker in CommandProblems)
        {
            int index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                int start = output.LastIndexOfAny(new[] { '\n', '\r' }, index) + 1;
                int end = output.IndexOfAny(new[] { '\n', '\r' }, index);
                return output.Substring(start, (end < 0 ? output.Length : end) - start).Trim();
            }
        }
        return null;
    }

    private static string Shorten(string text)
    {
        return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
    }

    // ---------- reading the board's answers ----------

    private static readonly string[] DownloadProblems =
    {
        "Retry count exceeded",
        "TFTP error",
        "TFTP Abort",
        "TFTP aborted",
        "Unknown command",
        "Wrong Image Format",
        "ARP Retry",
        "*** ERROR",
        "File not found",
        "Access violation"
    };

    /// <summary>The line of the board's output that says a TFTP download failed, or null.</summary>
    public static string FindDownloadProblem(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }
        foreach (string marker in DownloadProblems)
        {
            int index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                int start = output.LastIndexOfAny(new[] { '\n', '\r' }, index) + 1;
                int end = output.IndexOfAny(new[] { '\n', '\r' }, index);
                return output.Substring(start, (end < 0 ? output.Length : end) - start).Trim();
            }
        }
        return null;
    }

    /// <summary>True when the board says the download is complete.</summary>
    public static bool DownloadReported(string output)
    {
        return output != null && output.IndexOf("Bytes transferred", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

/// <summary>
/// Collects what the board prints and tells when it is back at its prompt, i.e. when the last command has finished and the
/// next one can be sent. The prompt is a last line that is not finished (no line break after it), and the board has been
/// quiet for a moment, so a "=>" inside the output does not count.
/// </summary>
public sealed class ConsoleOutputWatcher
{
    private const int MaxChars = 65536;

    private readonly object _lock = new object();
    private readonly StringBuilder _text = new StringBuilder();
    private int _lastDataTick;

    public void Reset()
    {
        lock (_lock)
        {
            _text.Clear();
        }
    }

    public void Append(string text, int nowTick)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        lock (_lock)
        {
            _text.Append(text);
            if (_text.Length > MaxChars)
            {
                _text.Remove(0, _text.Length - MaxChars / 2);
            }
            _lastDataTick = nowTick;
        }
    }

    public string Text
    {
        get
        {
            lock (_lock)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>True when the output ends with a prompt and nothing has arrived for <paramref name="quietMs"/>.</summary>
    /// <param name="customPrompt">What the prompt ends with (such as "=&gt;"); empty recognizes the usual ones.</param>
    public bool IsPromptReady(string customPrompt, int nowTick, int quietMs)
    {
        lock (_lock)
        {
            if (unchecked(nowTick - _lastDataTick) < quietMs)
            {
                return false;
            }
            return EndsWithPrompt(_text.ToString(), customPrompt);
        }
    }

    private static readonly Regex EscapeRegex = new Regex("\u001b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static bool EndsWithPrompt(string text, string customPrompt)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        string tail = text.Length > 300 ? text.Substring(text.Length - 300) : text;
        tail = EscapeRegex.Replace(tail, string.Empty);
        string line = tail.Substring(tail.LastIndexOfAny(new[] { '\n', '\r' }) + 1);
        if (line.Length == 0)
        {
            return false; // the output ended with a line break: a prompt is written after it, not before
        }

        string custom = customPrompt == null ? string.Empty : customPrompt.Trim();
        if (custom.Length > 0)
        {
            return line.TrimEnd().EndsWith(custom, StringComparison.Ordinal);
        }
        // "=> ", "U-Boot> ", "ath>", "MT7621 # ": a "#" needs its space, because the download's progress bar is made of "#".
        return line.EndsWith(">", StringComparison.Ordinal)
            || line.EndsWith("> ", StringComparison.Ordinal)
            || line.EndsWith("# ", StringComparison.Ordinal)
            || line.EndsWith("$ ", StringComparison.Ordinal);
    }
}
