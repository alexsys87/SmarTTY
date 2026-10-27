using System;
using System.Collections.Generic;
using System.Text;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// Turns what the person types or pastes into the bytes sent to a COM/TCP device: HEX input with the usual separators
/// and prefixes, and pasted text with its line endings brought to the device's line ending.
/// </summary>
public static class DeviceInputText
{
    /// <summary>Characters that separate bytes in HEX input: "41 42", "41,42", "41;42", "41:42", "41-42", one per line.</summary>
    public static bool IsHexSeparator(char ch)
    {
        return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == ',' || ch == ';' || ch == ':' || ch == '-';
    }

    public static int HexDigitValue(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            return ch - '0';
        }
        if (ch >= 'a' && ch <= 'f')
        {
            return ch - 'a' + 10;
        }
        if (ch >= 'A' && ch <= 'F')
        {
            return ch - 'A' + 10;
        }
        return -1;
    }

    /// <summary>
    /// Whether a typed character may go into the HEX input box: a digit, a separator, or the "x" of a "0x" prefix
    /// (control characters such as Backspace are always let through by the caller).
    /// </summary>
    public static bool IsHexInputCharacter(char ch)
    {
        return HexDigitValue(ch) != -1 || IsHexSeparator(ch) || ch == 'x' || ch == 'X';
    }

    /// <summary>
    /// Reads HEX input such as "41 42 0D 0A", "0x41,0x42", "41:42:43" or "414243". Inside one group the digits are
    /// paired from the left, so "414243" is three bytes and an odd last digit is a byte of its own ("123" is 12 03).
    /// </summary>
    /// <exception cref="FormatException">A character that is neither a HEX digit nor a separator, or a lone "0x".</exception>
    public static byte[] ParseHex(string text)
    {
        List<byte> bytes = new List<byte>();
        foreach (string group in SplitHexGroups(text))
        {
            string digits = StripHexPrefix(group);
            if (digits.Length == 0)
            {
                throw new FormatException("\"" + group + "\" has no HEX digits after the prefix.");
            }
            for (int i = 0; i < digits.Length; i += 2)
            {
                int high = HexDigitValue(digits[i]);
                if (high == -1)
                {
                    throw new FormatException("Invalid HEX character: " + digits[i]);
                }
                if (i + 1 == digits.Length)
                {
                    bytes.Add((byte)high);
                    break;
                }
                int low = HexDigitValue(digits[i + 1]);
                if (low == -1)
                {
                    throw new FormatException("Invalid HEX character: " + digits[i + 1]);
                }
                bytes.Add((byte)((high << 4) | low));
            }
        }
        return bytes.ToArray();
    }

    /// <summary>
    /// For "send immediately": true once the input holds a complete byte that will not change by typing on, that is the
    /// last group has two digits (prefix aside) or a separator follows a digit. Input that cannot be parsed is not ready:
    /// it is kept for the person to correct and is reported when sent with Enter.
    /// </summary>
    public static bool IsHexReadyToSend(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        List<string> groups = SplitHexGroups(text);
        if (groups.Count == 0)
        {
            return false;
        }
        try
        {
            ParseHex(text);
        }
        catch (FormatException)
        {
            return false;
        }
        if (IsHexSeparator(text[text.Length - 1]))
        {
            return true;
        }
        return StripHexPrefix(groups[groups.Count - 1]).Length >= 2;
    }

    /// <summary>
    /// Brings every line ending of pasted text (CR LF, a lone CR or a lone LF) to <paramref name="lineEnding"/>, so a
    /// multi-line paste reaches the device as if each line had been typed and sent with Enter. Null keeps the text as it is.
    /// </summary>
    public static string ConvertLineEndings(string text, string lineEnding)
    {
        if (string.IsNullOrEmpty(text) || lineEnding == null)
        {
            return text;
        }
        StringBuilder result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                result.Append(lineEnding);
            }
            else if (ch == '\n')
            {
                result.Append(lineEnding);
            }
            else
            {
                result.Append(ch);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Reads pasted text as a string of bytes: what <see cref="ParseHex"/> reads ("DE AD BE EF", "0xDE,0xAD", "DE:AD",
    /// "DEADBEEF") plus the forms found in code and logs: "\xDE\xAD" and a C array in braces, brackets or parentheses.
    /// Anything else, ordinary text above all, is not bytes: false. A group of several digits must have an even number of
    /// them ("123" is more likely a number than the bytes 12 03).
    /// </summary>
    public static bool TryParsePastedHex(string text, out byte[] bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        StringBuilder cleaned = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\\' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
            {
                cleaned.Append(' ');
                i++;
            }
            else if (ch == '{' || ch == '}' || ch == '[' || ch == ']' || ch == '(' || ch == ')')
            {
                cleaned.Append(' ');
            }
            else
            {
                cleaned.Append(ch);
            }
        }
        string normalized = cleaned.ToString();
        List<string> groups = SplitHexGroups(normalized);
        if (groups.Count == 0)
        {
            return false;
        }
        foreach (string group in groups)
        {
            string digits = StripHexPrefix(group);
            if (digits.Length == 0 || (digits.Length > 1 && digits.Length % 2 != 0))
            {
                return false;
            }
        }
        try
        {
            bytes = ParseHex(normalized);
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }
        return bytes.Length > 0;
    }

    private static List<string> SplitHexGroups(string text)
    {
        List<string> groups = new List<string>();
        if (text == null)
        {
            return groups;
        }
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool separator = i == text.Length || IsHexSeparator(text[i]);
            if (separator)
            {
                if (start != -1)
                {
                    groups.Add(text.Substring(start, i - start));
                    start = -1;
                }
            }
            else if (start == -1)
            {
                start = i;
            }
        }
        return groups;
    }

    private static string StripHexPrefix(string group)
    {
        if (group.Length >= 2 && group[0] == '0' && (group[1] == 'x' || group[1] == 'X'))
        {
            return group.Substring(2);
        }
        return group;
    }
}
