using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SmarTTY.CodeEditing;

/// <summary>
/// What the column of a "file:line:column" message counts. The tools disagree, and the editor counts characters, so the same
/// column is a different place in the line when it has a tab or a letter that is not ASCII in front of it.
/// </summary>
public enum ColumnUnit
{
    [EnumDisplayText("Detect from the command (gcc, make: tab stops; clang, go: bytes)")]
    Auto,

    [EnumDisplayText("Characters (rustc, Python, most others)")]
    Characters,

    [EnumDisplayText("Bytes (clang, go; a tab is one byte)")]
    Bytes,

    [EnumDisplayText("Screen columns, tab stops of 8 (gcc)")]
    TabStops
}

/// <summary>
/// Finds the place in a line that a column of a compiler message stands for, and picks the unit the command's tool uses.
///
/// gcc counts screen columns in every version: a tab goes to the next multiple of 8 (-ftabstop), and since gcc 11 a letter counts
/// as wide as it is shown. clang and go count bytes (a tab is one), rustc counts characters.
///
/// The class is free of any UI dependency, so the test harness can check it without a display.
/// </summary>
public static class CompilerColumns
{
    /// <summary>The distance between tab stops that gcc uses unless it is told otherwise (-ftabstop).</summary>
    public const int DefaultTabStop = 8;

    // arm-openwrt-linux-gcc, x86_64-linux-gnu-g++, gcc-13, cc, c++, gfortran
    private static readonly Regex GccName = new Regex(@"^(.*-)?(gcc|g\+\+|cc|c\+\+|cpp|gfortran|gnat\w*|gdc|gccgo)(-\d+(\.\d+)*)?$", RegexOptions.CultureInvariant);

    // clang, clang++, clang-15, aarch64-linux-gnu-clang, clang-tidy
    private static readonly Regex ClangName = new Regex(@"^(.*-)?clang(\+\+|-cl|-tidy)?(-\d+(\.\d+)*)?$", RegexOptions.CultureInvariant);

    // The build tools that run a gcc most of the time.
    private static readonly HashSet<string> BuildTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "make", "gmake", "bmake", "mingw32-make", "cmake", "ninja", "samu", "configure", "scons", "meson"
    };

    /// <summary>
    /// The unit to read columns in: <paramref name="setting"/> when it says (anything but Auto), otherwise the one the tools in
    /// the command line use: clang and go count bytes, gcc and the tools that run it count tab stops, anything else characters.
    /// A command that names clang and make (CC=clang make) is clang's.
    /// </summary>
    public static ColumnUnit Resolve(ColumnUnit setting, string commandText)
    {
        if (setting != ColumnUnit.Auto)
        {
            return setting;
        }
        bool gcc = false;
        bool go = false;
        foreach (string word in Words(commandText))
        {
            if (ClangName.IsMatch(word))
            {
                return ColumnUnit.Bytes;
            }
            if (word == "go")
            {
                go = true;
            }
            else if (GccName.IsMatch(word) || BuildTools.Contains(word))
            {
                gcc = true;
            }
        }
        if (go)
        {
            return ColumnUnit.Bytes;
        }
        return gcc ? ColumnUnit.TabStops : ColumnUnit.Characters;
    }

    // The words of a command line, in lower case and without the directory in front: "CC=/usr/bin/clang make" gives cc, clang, make.
    private static IEnumerable<string> Words(string commandText)
    {
        if (string.IsNullOrEmpty(commandText))
        {
            yield break;
        }
        foreach (string raw in commandText.Split(new[] { ' ', '\t', ';', '&', '|', '(', ')', '<', '>', '`', '"', '\'', '=', '$', '{', '}' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = raw.LastIndexOf('/');
            string word = (slash >= 0 ? raw.Substring(slash + 1) : raw).ToLowerInvariant();
            if (word.Length > 0)
            {
                yield return word;
            }
        }
    }

    /// <summary>
    /// The index in <paramref name="line"/> of the character that <paramref name="column"/> (counted from 0, in
    /// <paramref name="unit"/>) points at; the length of the line when the column is at its end or beyond.
    /// </summary>
    public static int ToCharIndex(string line, int column, ColumnUnit unit, int tabStop = DefaultTabStop)
    {
        if (column <= 0 || string.IsNullOrEmpty(line))
        {
            return 0;
        }
        if (tabStop < 1)
        {
            tabStop = DefaultTabStop;
        }
        int offset = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            bool pair = char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]);
            int codePoint = pair ? char.ConvertToUtf32(c, line[i + 1]) : c;
            int size;
            switch (unit)
            {
                case ColumnUnit.Bytes:
                    size = codePoint < 0x80 ? 1 : (codePoint < 0x800 ? 2 : (codePoint < 0x10000 ? 3 : 4));
                    break;
                case ColumnUnit.TabStops:
                    size = c == '\t' ? tabStop - (offset % tabStop) : Width(codePoint);
                    break;
                default:
                    size = pair ? 2 : 1;
                    break;
            }
            // The character that covers the column; a zero-width one (an accent) belongs to the letter before it.
            if (offset + size > column)
            {
                return i;
            }
            offset += size;
            if (pair)
            {
                i++;
            }
        }
        return line.Length;
    }

    /// <summary>How many screen columns a character takes: 0 for an accent that is drawn over the letter before it, 2 for a wide one.</summary>
    public static int Width(int codePoint)
    {
        if (codePoint < 0x300)
        {
            return 1;
        }
        UnicodeCategory category = codePoint < 0x10000
            ? CharUnicodeInfo.GetUnicodeCategory((char)codePoint)
            : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0);
        if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark || category == UnicodeCategory.Format)
        {
            return 0;
        }
        bool wide = (codePoint >= 0x1100 && codePoint <= 0x115F) || (codePoint >= 0x2E80 && codePoint <= 0x303E)
            || (codePoint >= 0x3041 && codePoint <= 0x33FF) || (codePoint >= 0x3400 && codePoint <= 0x4DBF)
            || (codePoint >= 0x4E00 && codePoint <= 0xA4CF) || (codePoint >= 0xAC00 && codePoint <= 0xD7A3)
            || (codePoint >= 0xF900 && codePoint <= 0xFAFF) || (codePoint >= 0xFE30 && codePoint <= 0xFE6F)
            || (codePoint >= 0xFF00 && codePoint <= 0xFF60) || (codePoint >= 0xFFE0 && codePoint <= 0xFFE6)
            || (codePoint >= 0x1F300 && codePoint <= 0x1F64F) || (codePoint >= 0x1F900 && codePoint <= 0x1F9FF)
            || (codePoint >= 0x20000 && codePoint <= 0x3FFFD);
        return wide ? 2 : 1;
    }
}
