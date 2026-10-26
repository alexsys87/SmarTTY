using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// The texts pasted into the terminals lately, newest first, for the paste menu. The list lives in memory only: pasted
/// text may hold a password or a token, so it is never written to a file, and it is gone when SmarTTY is closed.
/// Free of WinForms so that the test harness can compile it.
/// </summary>
public static class PasteHistory
{
    /// <summary>The most entries the history can keep, whatever the setting says.</summary>
    public const int MaxCapacity = 50;

    private static readonly object Sync = new object();

    private static readonly List<string> Entries = new List<string>();

    /// <summary>
    /// Puts <paramref name="text"/> at the top of the history (an equal entry moves up instead of being doubled) and
    /// drops the oldest entries beyond <paramref name="capacity"/>. A capacity of 0 or less keeps nothing.
    /// </summary>
    public static void Add(string text, int capacity)
    {
        if (capacity <= 0)
        {
            Trim(0);
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        lock (Sync)
        {
            Entries.RemoveAll(entry => string.Equals(entry, text, StringComparison.Ordinal));
            Entries.Insert(0, text);
            TrimLocked(Math.Min(capacity, MaxCapacity));
        }
    }

    /// <summary>The newest entries (at most <paramref name="count"/>), newest first.</summary>
    public static string[] Snapshot(int count)
    {
        lock (Sync)
        {
            int take = Math.Max(0, Math.Min(count, Entries.Count));
            return Entries.GetRange(0, take).ToArray();
        }
    }

    public static int Count
    {
        get
        {
            lock (Sync)
            {
                return Entries.Count;
            }
        }
    }

    public static void Clear()
    {
        Trim(0);
    }

    private static void Trim(int capacity)
    {
        lock (Sync)
        {
            TrimLocked(capacity);
        }
    }

    private static void TrimLocked(int capacity)
    {
        if (Entries.Count > capacity)
        {
            Entries.RemoveRange(capacity, Entries.Count - capacity);
        }
    }

    /// <summary>
    /// One-line text for a menu item: the start of the text with line breaks shown as an arrow, control characters
    /// and tabs as spaces, cut to <paramref name="maxLength"/> characters, and the number of lines of a longer text.
    /// </summary>
    public static string Preview(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        maxLength = Math.Max(maxLength, 4);
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int lines = 1;
        foreach (char ch in normalized.TrimEnd('\n'))
        {
            if (ch == '\n')
            {
                lines++;
            }
        }
        StringBuilder builder = new StringBuilder();
        bool cut = false;
        foreach (char ch in normalized.TrimEnd('\n'))
        {
            if (builder.Length >= maxLength)
            {
                cut = true;
                break;
            }
            if (ch == '\n')
            {
                builder.Append('\u21b5');
            }
            else if (ch == '\t' || char.IsControl(ch))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(ch);
            }
        }
        if (cut)
        {
            builder.Append('\u2026');
        }
        if (lines > 1)
        {
            builder.Append("  [").Append(lines).Append(" lines]");
        }
        return builder.ToString();
    }
}
