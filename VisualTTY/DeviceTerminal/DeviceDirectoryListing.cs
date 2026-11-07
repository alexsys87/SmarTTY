using System;
using System.Collections.Generic;
using System.Text;

namespace VisualTTY.DeviceTerminal;

/// <summary>One name listed by the shell of the device.</summary>
public sealed class DeviceDirectoryEntry
{
    public string Name { get; }

    public bool IsDirectory { get; }

    public DeviceDirectoryEntry(string name, bool isDirectory)
    {
        Name = name;
        IsDirectory = isDirectory;
    }
}

/// <summary>
/// Lists a directory of the device through its own shell, for the path completion of the COM/TCP terminal: the files
/// are on the device, not on this computer (the completion used to offer the folder of the program).
/// A command is typed to the shell; its echo, its output and the prompt that follows are taken out of the terminal
/// by <see cref="DeviceDirectoryCapture"/>. The output is framed by two marker lines that the command prints with printf
/// from two pieces, so the echo of the command line itself never contains a whole marker.
/// </summary>
public static class DeviceDirectoryListing
{
    public const string MarkerHead = "__SMARTTY_LS_";

    public static string BeginMarker(string token) => MarkerHead + "B" + token;

    public static string EndMarker(string token) => MarkerHead + "E" + token;

    /// <summary>
    /// The command that lists <paramref name="directory"/> (as typed: relative to the shell's directory, absolute, or
    /// starting with "~/"; empty for the shell's directory). It starts with a space, which keeps it out of the history of
    /// shells with HISTCONTROL=ignorespace. The listing runs in a subshell, so the directory of the shell does not change;
    /// "command ls" skips an alias such as ls --color, and -p marks directories with a slash.
    /// </summary>
    public static string BuildCommand(string directory, string token)
    {
        return " printf '%s%s\\n' " + MarkerHead + " 'B" + token + "';"
            + " (cd -- " + QuoteDirectory(directory) + " && command ls -1Ap) 2>/dev/null;"
            + " printf '%s%s\\n' " + MarkerHead + " 'E" + token + "'";
    }

    /// <summary>The directory as one word of the shell. "~" and "~/..." keep the tilde unquoted, so the shell expands it.</summary>
    public static string QuoteDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return ".";
        }
        if (directory == "~")
        {
            return "~";
        }
        if (directory.StartsWith("~/", StringComparison.Ordinal))
        {
            string rest = directory.Substring(2);
            return rest.Length == 0 ? "~/" : "~/" + Quote(rest);
        }
        return Quote(directory);
    }

    private static string Quote(string text)
    {
        return "'" + text.Replace("'", "'\\''") + "'";
    }

    /// <summary>The name in one line of "ls -1Ap" output, or null for a line that names nothing (empty, "./", "../").</summary>
    public static DeviceDirectoryEntry ParseLine(string line)
    {
        string text = StripControl(line);
        if (text.Length == 0)
        {
            return null;
        }
        bool isDirectory = text.EndsWith("/", StringComparison.Ordinal);
        string name = isDirectory ? text.TrimEnd('/') : text;
        if (name.Length == 0 || name == "." || name == "..")
        {
            return null;
        }
        return new DeviceDirectoryEntry(name, isDirectory);
    }

    /// <summary>The text without escape sequences (colours) and control characters.</summary>
    public static string StripControl(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        StringBuilder result = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch == '\u001b')
            {
                i = SkipEscapeSequence(text, i);
                continue;
            }
            if (ch >= ' ' && ch != '\u007f')
            {
                result.Append(ch);
            }
            i++;
        }
        return result.ToString();
    }

    // Returns the index after the escape sequence that starts at text[esc] (the text length when it is unfinished).
    private static int SkipEscapeSequence(string text, int esc)
    {
        int length = text.Length;
        if (esc + 1 >= length)
        {
            return length;
        }
        char kind = text[esc + 1];
        if (kind == '[')
        {
            int j = esc + 2;
            while (j < length && text[j] >= ' ' && text[j] <= '?')
            {
                j++;
            }
            return j >= length ? length : j + 1;
        }
        if (kind == ']')
        {
            // OSC: ends with BEL or ESC \.
            for (int j = esc + 2; j < length; j++)
            {
                if (text[j] == '\a')
                {
                    return j + 1;
                }
                if (text[j] == '\u001b' && j + 1 < length && text[j + 1] == '\\')
                {
                    return j + 2;
                }
            }
            return length;
        }
        return Math.Min(esc + 2, length);
    }
}

/// <summary>
/// Takes the answer to <see cref="DeviceDirectoryListing.BuildCommand"/> out of what the device sends: the echo of the
/// command and everything up to the begin marker, the listed names, the end marker and the shell prompt after it.
/// Nothing of it is meant for the screen; if the answer never comes, <see cref="Abort"/> gives the text back.
/// </summary>
public sealed class DeviceDirectoryCapture
{
    private enum State
    {
        WaitingForBegin,
        Collecting,
        WaitingForPrompt,
        Done
    }

    private readonly string _beginMarker;
    private readonly string _endMarker;
    private readonly StringBuilder _received = new StringBuilder();
    private readonly StringBuilder _line = new StringBuilder();
    private readonly StringBuilder _tail = new StringBuilder();
    private readonly List<DeviceDirectoryEntry> _entries = new List<DeviceDirectoryEntry>();
    private State _state = State.WaitingForBegin;

    public DeviceDirectoryCapture(string token)
    {
        _beginMarker = DeviceDirectoryListing.BeginMarker(token);
        _endMarker = DeviceDirectoryListing.EndMarker(token);
    }

    /// <summary>The end marker has arrived: the listing is complete (the prompt after it may still be on its way).</summary>
    public bool HasListing => _state == State.WaitingForPrompt || _state == State.Done;

    /// <summary>The prompt after the listing has arrived too: what the device sends from now on is shown again.</summary>
    public bool IsDone => _state == State.Done;

    public IReadOnlyList<DeviceDirectoryEntry> Entries => _entries;

    /// <summary>Takes received text; returns the part of it that is not the answer and must be shown (text after the prompt).</summary>
    public string Feed(string text)
    {
        if (string.IsNullOrEmpty(text) || _state == State.Done)
        {
            return text ?? string.Empty;
        }
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (_state == State.WaitingForPrompt)
            {
                _tail.Append(ch);
                if (LineInputText.EndsLikePrompt(_tail.ToString(), null))
                {
                    _state = State.Done;
                    // What follows the prompt in the same chunk (a cursor position query is cut with it) belongs to the screen.
                    string rest = text.Substring(i + 1);
                    return LineInputText.TrimTrailingControlSequences(rest).Length == 0 ? string.Empty : rest;
                }
                continue;
            }
            _received.Append(ch);
            if (ch != '\n')
            {
                _line.Append(ch);
                continue;
            }
            string line = DeviceDirectoryListing.StripControl(_line.ToString());
            _line.Clear();
            if (_state == State.WaitingForBegin)
            {
                if (line.EndsWith(_beginMarker, StringComparison.Ordinal))
                {
                    _state = State.Collecting;
                }
            }
            else if (line.EndsWith(_endMarker, StringComparison.Ordinal))
            {
                _state = State.WaitingForPrompt;
            }
            else
            {
                DeviceDirectoryEntry entry = DeviceDirectoryListing.ParseLine(line);
                if (entry != null)
                {
                    _entries.Add(entry);
                }
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Ends the capture. Returns the text taken so far when the listing never completed (the device did not answer the
    /// way a shell does, so its text must not be lost), or nothing when only the prompt after the listing was missing.
    /// </summary>
    public string Abort()
    {
        State state = _state;
        _state = State.Done;
        return state == State.WaitingForBegin || state == State.Collecting ? _received.ToString() : string.Empty;
    }
}
