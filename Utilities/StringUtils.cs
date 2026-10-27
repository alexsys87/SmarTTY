using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Utilities;

public static class StringUtils
{
    public enum NumberFormatOptions
    {
        None = 0,
        Hex = 1,
        Kilo = 2,
        Mega = 4,
        Giga = 8,
        AllSuffixes = 14
    }

    public delegate string PlaceholderResolver(string name, string option);

    public static string FormatSize(long byteCount, bool appendUnit = false)
    {
        string suffix = "";
        if (byteCount > 10737418240L)
        {
            byteCount /= 1073741824;
            suffix = "G";
        }
        else if (byteCount > 10485760)
        {
            byteCount /= 1048576;
            suffix = "M";
        }
        else if (byteCount > 10240)
        {
            byteCount /= 1024;
            suffix = "K";
        }
        if (appendUnit)
        {
            suffix = ((!(suffix == "")) ? (suffix + "B") : " bytes");
        }
        return byteCount + suffix;
    }

    public static KeyValuePair<string, string> SplitAtFirst(string text, char ch, string defaultKey = null, string defaultValue = null)
    {
        int index = text?.IndexOf(ch) ?? (-1);
        if (index == -1)
        {
            return new KeyValuePair<string, string>(defaultKey, defaultValue);
        }
        return new KeyValuePair<string, string>(text.Substring(0, index), text.Substring(index + 1));
    }

    public static string ExpandPlaceholders(string format, PlaceholderResolver callback, char ch = ':')
    {
        if (format == null)
        {
            return null;
        }
        int braceDepth = 0;
        int placeholderStart = -1;
        StringBuilder stringBuilder = new StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] == '}' && i < format.Length - 1 && format[i + 1] == '}')
            {
                stringBuilder.Append(format[i]);
                i++;
            }
            else if (format[i] == '{' && i < format.Length - 1 && format[i + 1] == '{')
            {
                stringBuilder.Append(format[i]);
                i++;
            }
            else if (format[i] == '{')
            {
                if (braceDepth == 0)
                {
                    placeholderStart = i;
                }
                braceDepth++;
            }
            else if (format[i] == '}')
            {
                if (braceDepth <= 0)
                {
                    throw new Exception("Unexpected '}' in format string");
                }
                if (--braceDepth == 0)
                {
                    string placeholderName = format.Substring(placeholderStart + 1, i - placeholderStart - 1);
                    int separatorIndex = -2;
                    do
                    {
                        separatorIndex = placeholderName.IndexOf(ch, separatorIndex + 2);
                    }
                    while (separatorIndex != -1 && separatorIndex < placeholderName.Length - 2 && placeholderName[separatorIndex + 1] == ch);
                    string placeholderOption = null;
                    if (separatorIndex != -1)
                    {
                        placeholderOption = placeholderName.Substring(separatorIndex + 1);
                        placeholderName = placeholderName.Substring(0, separatorIndex);
                    }
                    if (placeholderName.Contains("{"))
                    {
                        placeholderName = ExpandPlaceholders(placeholderName, callback, ch);
                    }
                    stringBuilder.Append(callback(placeholderName, placeholderOption));
                }
            }
            else if (braceDepth == 0)
            {
                stringBuilder.Append(format[i]);
            }
        }
        return stringBuilder.ToString();
    }

    public static int CommonPrefixLength(string text, string value)
    {
        int limit = Math.Min(text.Length, value.Length);
        for (int i = 0; i < limit; i++)
        {
            if (char.ToLower(text[i]) != char.ToLower(value[i]))
            {
                return i;
            }
        }
        return limit;
    }

    public static bool TryParseNumber(string text, out ulong value)
    {
        ulong? number = ParseNumber(text);
        if (number.HasValue)
        {
            value = number.Value;
            return true;
        }
        value = 0uL;
        return false;
    }

    public static ulong? ParseNumber(string text)
    {
        if (text == null)
        {
            return null;
        }
        // Blanks around the number are not part of it; without this " 12" would be cut at the first blank to nothing.
        text = text.Trim();
        int spaceIndex = text.IndexOf(' ');
        if (spaceIndex != -1)
        {
            text = text.Substring(0, spaceIndex);
        }
        ulong value;
        if (text.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase))
        {
            if (!ulong.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, null, out value))
            {
                return null;
            }
            return value;
        }
        if (!ulong.TryParse(text, out value))
        {
            return null;
        }
        return value;
    }

    public static ulong? ParseSize(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        ulong multiplier = char.ToUpper(text[text.Length - 1]) switch
        {
            'K' => 1024uL,
            'M' => 1048576uL,
            'G' => 1073741824uL,
            _ => 1uL,
        };
        if (multiplier != 1)
        {
            text = text.Substring(0, text.Length - 1);
        }
        return ParseNumber(text) * multiplier;
    }
}
