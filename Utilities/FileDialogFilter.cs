using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Utilities;

/// <summary>
/// The filter strings that the file dialogs use ("All files (*.*)|*.*|Text (*.txt)|*.txt"). Both the WinForms and the
/// WPF file dialogs take that same text, so the parsing and the matching live here, away from any window, and can be
/// checked without a display. The themed dialog (<c>GUI/LocalFileDialogWindow</c>) uses them to list and to filter.
/// </summary>
public static class FileDialogFilter
{
    /// <summary>
    /// Splits a filter string into its entries. Every entry is a description and the patterns it stands for; the
    /// separators are "|" between the fields and ";" between the patterns of one entry. A trailing "|" and an
    /// incomplete pair at the end are ignored, so the strings the application already uses parse as they are.
    /// </summary>
    public static List<FileDialogFilterEntry> Parse(string filter)
    {
        List<FileDialogFilterEntry> entries = new List<FileDialogFilterEntry>();
        if (string.IsNullOrEmpty(filter))
        {
            return entries;
        }
        string[] fields = filter.Split('|');
        for (int index = 0; index + 1 < fields.Length; index += 2)
        {
            string description = fields[index];
            List<string> patterns = SplitPatterns(fields[index + 1]);
            if (description.Length == 0 && patterns.Count == 0)
            {
                continue;
            }
            entries.Add(new FileDialogFilterEntry(description, patterns));
        }
        return entries;
    }

    private static List<string> SplitPatterns(string field)
    {
        List<string> patterns = new List<string>();
        foreach (string part in field.Split(';'))
        {
            string trimmed = part.Trim();
            if (trimmed.Length != 0)
            {
                patterns.Add(trimmed);
            }
        }
        return patterns;
    }

    /// <summary>
    /// The entry a 1-based filter index points at, or null when the index is out of range. The dialogs pass the
    /// <c>FilterIndex</c> they were given (1 = the first entry), so the window can preselect it.
    /// </summary>
    public static FileDialogFilterEntry EntryAt(IList<FileDialogFilterEntry> entries, int filterIndex)
    {
        if (entries == null || filterIndex < 1 || filterIndex > entries.Count)
        {
            return null;
        }
        return entries[filterIndex - 1];
    }

    /// <summary>
    /// True when <paramref name="fileName"/> is matched by any of the patterns of the entry. A missing entry, or one
    /// without patterns, matches everything (an empty filter shows all files).
    /// </summary>
    public static bool MatchesEntry(FileDialogFilterEntry entry, string fileName)
    {
        if (entry == null || entry.Patterns.Count == 0)
        {
            return true;
        }
        foreach (string pattern in entry.Patterns)
        {
            if (Matches(pattern, fileName))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="fileName"/> matches a shell wildcard pattern ("*" any run, "?" one character).
    /// "*.*" and "*" match everything; the comparison ignores case, as the file systems the dialog browses do.
    /// </summary>
    public static bool Matches(string pattern, string fileName)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }
        if (pattern == "*" || pattern == "*.*")
        {
            return true;
        }
        if (fileName == null)
        {
            return false;
        }
        return Regex.IsMatch(fileName, "^" + ToRegex(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ToRegex(string pattern)
    {
        StringBuilder builder = new StringBuilder(pattern.Length + 8);
        foreach (char character in pattern)
        {
            switch (character)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }
        return builder.ToString();
    }
}

/// <summary>One entry of a file-dialog filter: the text shown in the type box and the patterns behind it.</summary>
public sealed class FileDialogFilterEntry
{
    /// <summary>The text shown in the "file type" box ("Text log (*.log)").</summary>
    public string Description { get; }

    /// <summary>The patterns of the entry ("*.log"), in the order they were written.</summary>
    public IReadOnlyList<string> Patterns { get; }

    public FileDialogFilterEntry(string description, IReadOnlyList<string> patterns)
    {
        Description = description;
        Patterns = patterns ?? Array.Empty<string>();
    }

    public override string ToString()
    {
        return Description;
    }
}
