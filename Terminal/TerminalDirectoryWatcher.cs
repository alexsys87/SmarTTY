using System;
using System.Text.RegularExpressions;

namespace SmarTTY.Terminal;

/// <summary>The shell that wrote a prompt: they take different commands to change the directory.</summary>
public enum PromptShell
{
    Unknown,

    /// <summary>cmd.exe: "C:\Users\me&gt;".</summary>
    Cmd,

    /// <summary>PowerShell: "PS C:\Users\me&gt; ".</summary>
    PowerShell
}

/// <summary>
/// Finds out which directory the shell of a Windows host is in, from what the host writes to the terminal. cmd.exe and
/// PowerShell tell the client nothing about their directory, so the file list next to the terminal could not follow a "cd".
/// Two things are recognised, and the watcher itself types nothing into the shell of the user (<see cref="TerminalDirectorySync"/>
/// does, and only at an empty prompt):
/// the escape sequences that terminals know for it (OSC 9;9 of Windows Terminal and ConEmu, OSC 7), if the prompt of the
/// user sends them, and otherwise the standard prompts, "C:\Users\me>" of cmd.exe and "PS C:\Users\me>" of PowerShell,
/// at the start of a line. A prompt of another shape is not understood, and the list then stays where it is.
/// </summary>
public sealed class TerminalDirectoryWatcher
{
    private const int MaxCarry = 4096;

    private const int MaxLine = 1024;

    private static readonly Regex Osc = new Regex("\u001b\\](?<code>[0-9]+);(?<text>[^\u0007\u001b]*)(?:\u0007|\u001b\\\\)", RegexOptions.Compiled);

    private static readonly Regex AnyOsc = new Regex("\u001b\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)", RegexOptions.Compiled);

    private static readonly Regex Csi = new Regex("\u001b\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private static readonly Regex ShortEscape = new Regex("\u001b(?:[()*+#%][ -~]|[@-Z\\\\-_=>])", RegexOptions.Compiled);

    private static readonly Regex Control = new Regex("[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]", RegexOptions.Compiled);

    // A drive path; the characters Windows does not allow in a name end it, so that "PS HKLM:\SOFTWARE>" and a UNC path are not taken.
    private static readonly Regex Prompt = new Regex("^(?:PS )?(?<path>[A-Za-z]:\\\\[^<>:\"|?*\\n]*)>", RegexOptions.Compiled | RegexOptions.Multiline);

    // The line of the cursor is a prompt and nothing else: the shell waits for a command and nothing has been typed.
    private static readonly Regex IdlePrompt = new Regex("^(?<ps>PS )?(?<path>[A-Za-z]:\\\\[^<>:\"|?*\\n]*)> ?$", RegexOptions.Compiled);

    private static readonly Regex FileUrl = new Regex("^file://[^/]*(?<path>/.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string _carry = "";

    // The line that has not ended yet, as text: a prompt can arrive in pieces ("C:\Us", "ers\me>").
    private string _line = "";

    private string _last;

    /// <summary>
    /// The directory of the shell, as the host wrote it ("C:\Users\me", or "/C:/Users/me" from OSC 7), when it is not the one
    /// reported last.
    /// </summary>
    public event Action<string> DirectoryReported;

    /// <summary>
    /// True while the cursor stands behind a prompt of cmd.exe or PowerShell and nothing has been typed after it: the one moment
    /// when text can be sent to the shell without landing in a program that is running or in a line being typed.
    /// </summary>
    public bool AtPrompt { get; private set; }

    /// <summary>The directory named by the prompt that <see cref="AtPrompt"/> is about.</summary>
    public string PromptDirectory { get; private set; }

    /// <summary>The shell that wrote that prompt.</summary>
    public PromptShell Shell { get; private set; }

    /// <summary>Raised when the shell has shown a new prompt and waits (<see cref="AtPrompt"/> has become true, or a prompt was shown again).</summary>
    public event Action PromptReady;

    /// <summary>Looks at a chunk of the output of the host, escape sequences included. Chunks may end anywhere.</summary>
    public void Observe(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }
        string text = _carry + chunk;
        _carry = "";
        int unfinished = FindUnfinishedEscape(text);
        if (unfinished >= 0)
        {
            if (text.Length - unfinished <= MaxCarry)
            {
                _carry = text.Substring(unfinished);
            }
            text = text.Substring(0, unfinished);
        }
        foreach (Match match in Osc.Matches(text))
        {
            string directory = DirectoryOfOsc(int.Parse(match.Groups["code"].Value), match.Groups["text"].Value);
            if (directory != null)
            {
                Report(directory);
            }
        }
        text = AnyOsc.Replace(text, "");
        // A sequence that puts the cursor on another line (cls clears the screen and goes home, PSReadLine redraws lines) starts
        // a new line for what follows, as a newline would; the others (colours, cursor visibility) are just dropped.
        text = Csi.Replace(text, match => "ABEFHJdf".IndexOf(match.Value[match.Value.Length - 1]) >= 0 ? "\n" : "");
        text = ShortEscape.Replace(text, match => match.Value.Length == 2 && "DEM".IndexOf(match.Value[1]) >= 0 ? "\n" : "");
        string cleaned = Control.Replace(text, "").Replace("\r\n", "\n").Replace('\r', '\n');
        // A line break in this chunk means that the line the cursor is on now is another one than before.
        bool lineBreak = cleaned.IndexOf('\n') >= 0;
        text = _line + cleaned;
        int lineStart = text.LastIndexOf('\n') + 1;
        _line = text.Length - lineStart <= MaxLine ? text.Substring(lineStart) : "";
        Match last = null;
        foreach (Match match in Prompt.Matches(text))
        {
            last = match;
        }
        if (last != null)
        {
            Report(last.Groups["path"].Value);
        }
        Match idle = IdlePrompt.Match(_line);
        bool wasAtPrompt = AtPrompt;
        AtPrompt = idle.Success;
        if (idle.Success)
        {
            PromptDirectory = idle.Groups["path"].Value;
            Shell = idle.Groups["ps"].Success ? PromptShell.PowerShell : PromptShell.Cmd;
            if (!wasAtPrompt || lineBreak)
            {
                // A new prompt: also when Enter and the prompt after it came in one chunk, with no typed text in between.
                PromptReady?.Invoke();
            }
        }
    }

    private void Report(string directory)
    {
        if (directory == _last)
        {
            return;
        }
        _last = directory;
        DirectoryReported?.Invoke(directory);
    }

    /// <summary>The directory an OSC sequence names: 9;9 (Windows Terminal, ConEmu) or 7 (file://host/path); null for anything else.</summary>
    internal static string DirectoryOfOsc(int code, string text)
    {
        if (code == 9 && text.StartsWith("9;", StringComparison.Ordinal))
        {
            string path = text.Substring(2).Trim().Trim('"');
            return Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]") ? path : null;
        }
        if (code == 7)
        {
            Match match = FileUrl.Match(text.Trim());
            if (!match.Success)
            {
                return null;
            }
            string path;
            try
            {
                path = Uri.UnescapeDataString(match.Groups["path"].Value);
            }
            catch (UriFormatException)
            {
                return null;
            }
            return Regex.IsMatch(path, "^/[A-Za-z]:") ? path : null;
        }
        return null;
    }

    /// <summary>
    /// The directory a Unix shell names with OSC 7 ("file://host/home/me/src", percent-encoded): the path, or null for anything else,
    /// a Windows drive path ("/C:/Users", see <see cref="DirectoryOfOsc"/>) included. Many distributions set it in the prompt of bash
    /// and zsh (/etc/profile.d/vte.sh), and it is what a link in the output of a command is resolved against.
    /// </summary>
    internal static string UnixDirectoryOfOsc(int code, string text)
    {
        if (code != 7 || text == null)
        {
            return null;
        }
        Match match = FileUrl.Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }
        string path;
        try
        {
            path = Uri.UnescapeDataString(match.Groups["path"].Value);
        }
        catch (UriFormatException)
        {
            return null;
        }
        if (path.Length == 0 || path[0] != '/' || Regex.IsMatch(path, "^/[A-Za-z]:") || path.IndexOf('\0') >= 0)
        {
            return null;
        }
        string trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static bool HasOscTerminator(string text, int from)
    {
        for (int index = from; index < text.Length; index++)
        {
            if (text[index] == '\u0007' || (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\'))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The index of an escape sequence that the text ends in the middle of, or -1.</summary>
    private static int FindUnfinishedEscape(string text)
    {
        // An OSC whose terminator is cut in two (ESC at the end of the chunk, the backslash in the next) starts earlier than the last ESC.
        int osc = text.LastIndexOf("\u001b]", StringComparison.Ordinal);
        if (osc >= 0 && !HasOscTerminator(text, osc + 2))
        {
            return osc;
        }
        int esc = text.LastIndexOf('\u001b');
        if (esc < 0)
        {
            return -1;
        }
        if (esc + 1 >= text.Length)
        {
            return esc;
        }
        char kind = text[esc + 1];
        if (kind == '[')
        {
            for (int index = esc + 2; index < text.Length; index++)
            {
                if (text[index] >= '@' && text[index] <= '~')
                {
                    return -1;
                }
            }
            return esc;
        }
        if (kind == ']')
        {
            return HasOscTerminator(text, esc + 2) ? -1 : esc;
        }
        if ("()*+#%".IndexOf(kind) >= 0 && esc + 2 >= text.Length)
        {
            return esc;
        }
        return -1;
    }
}
