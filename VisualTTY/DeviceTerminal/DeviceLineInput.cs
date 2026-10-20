using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// Commands sent from a COM/TCP terminal, newest last. Consecutive and older duplicates are
/// collapsed (a repeated command moves to the end), blank lines are ignored, and a line that starts with a space is
/// not recorded (the same convention as HISTCONTROL=ignorespace), which keeps a command out of the saved history.
/// </summary>
public class CommandHistory
{
    public const int DefaultCapacity = 200;

    private readonly List<string> _items = new List<string>();

    public int Capacity { get; }

    public IReadOnlyList<string> Items => _items;

    public int Count => _items.Count;

    public CommandHistory(int capacity = DefaultCapacity)
    {
        Capacity = Math.Max(1, capacity);
    }

    /// <summary>Records a sent line. Returns true when the history changed.</summary>
    public bool Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0]))
        {
            return false;
        }
        line = line.TrimEnd();
        int existing = _items.IndexOf(line);
        if (existing >= 0 && existing == _items.Count - 1)
        {
            return false;
        }
        if (existing >= 0)
        {
            _items.RemoveAt(existing);
        }
        _items.Add(line);
        if (_items.Count > Capacity)
        {
            _items.RemoveRange(0, _items.Count - Capacity);
        }
        return true;
    }

    public void Clear()
    {
        _items.Clear();
    }

    public static CommandHistory Parse(string text, int capacity = DefaultCapacity)
    {
        CommandHistory history = new CommandHistory(capacity);
        foreach (string line in LineInputText.SplitLines(text ?? string.Empty))
        {
            history.Add(line);
        }
        return history;
    }

    public string Serialize()
    {
        StringBuilder text = new StringBuilder();
        foreach (string item in _items)
        {
            text.Append(item).Append('\n');
        }
        return text.ToString();
    }
}

/// <summary>Plain-text lists: one command per line, empty lines and lines starting with '#' are comments.</summary>
public static class CommandListFile
{
    public static List<string> Parse(string text)
    {
        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in LineInputText.SplitLines(text ?? string.Empty))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (seen.Add(line))
            {
                result.Add(line);
            }
        }
        return result;
    }

    public static string Format(IEnumerable<string> commands)
    {
        StringBuilder text = new StringBuilder();
        foreach (string command in Parse(string.Join("\n", commands ?? Enumerable.Empty<string>())))
        {
            text.Append(command).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// Suggestions for the input box: recently used commands first (newest first), then the commands of this device,
    /// then the commands shared by all devices. Duplicates keep their first (highest) position.
    /// </summary>
    public static List<string> BuildSuggestions(IEnumerable<string> history, IEnumerable<string> deviceCommands, IEnumerable<string> commonCommands)
    {
        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string item in (history ?? Enumerable.Empty<string>()).Reverse())
        {
            if (!string.IsNullOrWhiteSpace(item) && seen.Add(item))
            {
                result.Add(item);
            }
        }
        foreach (string item in (deviceCommands ?? Enumerable.Empty<string>()).Concat(commonCommands ?? Enumerable.Empty<string>()))
        {
            if (!string.IsNullOrWhiteSpace(item) && seen.Add(item))
            {
                result.Add(item);
            }
        }
        return result;
    }
}

/// <summary>Helpers for the command lines of a COM/TCP terminal.</summary>
public static class LineInputText
{
    private static readonly string[] SecretPromptWords = { "password", "passphrase", "passcode", "passwd", "pin:", "secret", "парол" };

    /// <summary>Splits on CRLF, LF or CR. A trailing line break does not produce an empty last element.</summary>
    public static List<string> SplitLines(string text)
    {
        List<string> lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' || text[i] == '\n')
            {
                lines.Add(text.Substring(start, i - start));
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            lines.Add(text.Substring(start));
        }
        return lines;
    }

    private static readonly string[] PromptEndings = { "> ", "# ", "$ ", ">> " };

    /// <summary>
    /// True when received text ends the way a shell prompt does: with the current prompt or with one of the usual
    /// endings ("&gt; ", "# ", "$ ", "&gt;&gt; "). Only line breaks are trimmed at the end: the blank after the
    /// prompt character is part of the pattern. This says nothing about whether a program is running that merely
    /// prints such text; see <c>RemoteProgramTracker</c>.
    /// </summary>
    public static bool EndsLikePrompt(string text, string currentPrompt)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        string tail = text.TrimEnd('\r', '\n');
        if (!string.IsNullOrEmpty(currentPrompt) && tail.EndsWith(currentPrompt, StringComparison.Ordinal))
        {
            return true;
        }
        foreach (string ending in PromptEndings)
        {
            if (tail.EndsWith(ending, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when received text ends the way the prompt of a Linux shell does ("# " or "$ "). A bootloader or a
    /// device console ends its prompt differently ("=> ", "&gt; "), a login or password prompt with a colon.
    /// </summary>
    public static bool EndsLikeShellPrompt(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        string tail = text.TrimEnd('\r', '\n');
        return tail.EndsWith("# ", StringComparison.Ordinal) || tail.EndsWith("$ ", StringComparison.Ordinal);
    }

    /// <summary>
    /// The command that tells a shell on the device the size of the terminal window, or null when the size is not
    /// known yet. A serial line (and a raw TCP connection) has no way to carry the size, so programs such as mc and
    /// htop would draw into the default 80x24 corner; telnet and SSH pass the size on by themselves.
    /// Two things are set because neither is on every device: the COLUMNS and LINES variables, which ncurses (htop)
    /// and slang (mc) use when the tty reports no size - a serial tty does not - and the size of the tty itself
    /// with stty. The stty applet is missing from many BusyBox builds (OpenWrt), hence "2>/dev/null": no error is
    /// shown when it is not there, the variables are enough.
    /// </summary>
    public static string BuildRemoteSizeCommand(int columns, int rows)
    {
        if (columns < 1 || rows < 1)
        {
            return null;
        }
        columns = Math.Min(columns, 1000);
        rows = Math.Min(rows, 1000);
        return "export COLUMNS=" + columns + " LINES=" + rows + "; stty cols " + columns + " rows " + rows + " 2>/dev/null";
    }

    /// <summary>
    /// True when the prompt the device printed asks for a secret ("Password:", "Enter passphrase", "PIN:" ...).
    /// What is typed at such a prompt is not stored in the saved history.
    /// </summary>
    public static bool LooksLikeSecretPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }
        foreach (string word in SecretPromptWords)
        {
            if (prompt.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Where the line-input data lives: <c>DeviceLineInput/History/&lt;device&gt;.txt</c> and
/// <c>DeviceLineInput/Commands/&lt;device&gt;.txt</c> (plus <c>_common.txt</c> shared by all devices) under the
/// SmarTTY data directory. Everything is best effort: a missing or unreadable file is an empty list, a failed save
/// never breaks the terminal.
/// </summary>
public class DeviceLineInputStorage
{
    public const string RootDirectoryName = "DeviceLineInput";

    public const string CommonCommandsFileName = "_common.txt";

    private readonly string _root;

    public DeviceLineInputStorage(string dataDirectory)
    {
        _root = Path.Combine(dataDirectory, RootDirectoryName);
    }

    /// <summary>
    /// Key of a serial device. It is built like the exclusive key of the tab, so "COM3", "com3" and "\\.\COM3" are one device.
    /// </summary>
    public static string MakeSerialKey(string comPortName)
    {
        string key = DeviceSessionKey.ForSerialPort(comPortName);
        return key == null ? null : Sanitize(key);
    }

    /// <summary>Key of a raw TCP or telnet device: host and port.</summary>
    public static string MakeTcpKey(string host, int port)
    {
        return string.IsNullOrWhiteSpace(host) ? null : Sanitize("tcp-" + host.Trim().ToLowerInvariant() + "-" + port);
    }

    private static string Sanitize(string raw)
    {
        StringBuilder key = new StringBuilder();
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char ch in raw)
        {
            key.Append(Array.IndexOf(invalid, ch) >= 0 || ch == ' ' || ch == ':' ? '_' : ch);
        }
        return key.Length > 100 ? key.ToString(0, 100) : key.ToString();
    }

    public string HistoryPath(string key)
    {
        return Path.Combine(_root, "History", key + ".txt");
    }

    /// <summary>Path of the command list of one device, or of the list shared by all devices when key is null.</summary>
    public string CommandsPath(string key)
    {
        return Path.Combine(_root, "Commands", key == null ? CommonCommandsFileName : key + ".txt");
    }

    public CommandHistory LoadHistory(string key)
    {
        return CommandHistory.Parse(ReadAll(key == null ? null : HistoryPath(key)));
    }

    public bool SaveHistory(string key, CommandHistory history)
    {
        return key != null && WriteAll(HistoryPath(key), history.Serialize());
    }

    public bool DeleteHistory(string key)
    {
        try
        {
            string path = key == null ? null : HistoryPath(key);
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public List<string> LoadCommands(string key)
    {
        return CommandListFile.Parse(ReadAll(CommandsPath(key)));
    }

    public bool SaveCommands(string key, IEnumerable<string> commands)
    {
        return WriteAll(CommandsPath(key), CommandListFile.Format(commands));
    }

    private static string ReadAll(string path)
    {
        try
        {
            return path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool WriteAll(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temp, path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
