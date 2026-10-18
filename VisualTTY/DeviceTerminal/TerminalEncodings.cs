using System;
using System.Collections.Generic;
using System.Text;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// Text encodings offered for COM and raw TCP terminals. The name is what gets stored in
/// <see cref="DeviceTerminalSettings.TextEncoding"/>; it is a standard encoding name that
/// <see cref="Encoding.GetEncoding(string)"/> understands.
/// </summary>
public static class TerminalEncodings
{
    public sealed class Choice
    {
        public Choice(string name, string displayName)
        {
            Name = name;
            DisplayName = displayName;
        }

        public string Name { get; }

        public string DisplayName { get; }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public const string DefaultName = "utf-8";

    public static IReadOnlyList<Choice> All { get; } = new Choice[]
    {
        new Choice("utf-8", "UTF-8"),
        new Choice("windows-1251", "Windows-1251 (Cyrillic)"),
        new Choice("cp866", "CP866 (DOS Cyrillic)"),
        new Choice("koi8-r", "KOI8-R (Cyrillic)"),
        new Choice("iso-8859-5", "ISO 8859-5 (Cyrillic)"),
        new Choice("us-ascii", "ASCII (7-bit)")
    };

    /// <summary>
    /// Returns the encoding for a stored name. An empty, unknown or unsupported name (for example from
    /// a hand-edited settings file) falls back to UTF-8 instead of breaking the terminal.
    /// </summary>
    public static Encoding Resolve(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                return Encoding.GetEncoding(name);
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
        return Encoding.UTF8;
    }
}
