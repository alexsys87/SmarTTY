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

    public struct FirstTokenSplit
    {
        public string First;

        public string Rest;
    }

    public static string AfterFirst(string text, char ch, string defaultValue = null)
    {
        if (text == null)
        {
            return null;
        }
        int index = text.IndexOf(ch);
        if (index == -1)
        {
            return defaultValue;
        }
        return text.Substring(index + 1);
    }

    public static IEnumerable<int> IndexesOf(string text, char ch)
    {
        int startIndex = 0;
        while (true)
        {
            int index = text.IndexOf(ch, startIndex);
            if (index != -1)
            {
                yield return index;
                startIndex = index + 1;
                continue;
            }
            break;
        }
    }

    public static string AfterLast(string text, char ch, string defaultValue = null)
    {
        if (text == null)
        {
            return null;
        }
        int index = text.LastIndexOf(ch);
        if (index == -1)
        {
            return defaultValue;
        }
        return text.Substring(index + 1);
    }

    public static string BeforeFirst(string text, char ch, string defaultValue = null)
    {
        if (text == null)
        {
            return null;
        }
        int index = text.IndexOf(ch);
        if (index == -1)
        {
            return defaultValue;
        }
        return text.Substring(0, index);
    }

    public static string BeforeLast(string text, char ch, string defaultValue = null)
    {
        if (text == null)
        {
            return null;
        }
        int index = text.LastIndexOf(ch);
        if (index == -1)
        {
            return defaultValue;
        }
        return text.Substring(0, index);
    }

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

    private static void TryReduceUnit(ref ulong value, ref string suffix, ulong threshold, string unitSuffix, NumberFormatOptions numberFormatOptions, NumberFormatOptions targetOption)
    {
        if (string.IsNullOrEmpty(suffix) && (numberFormatOptions & targetOption) != NumberFormatOptions.None && value % threshold == 0L)
        {
            value /= threshold;
            suffix = unitSuffix;
        }
    }

    public static string FormatNumber(ulong number, NumberFormatOptions numberFormatOptions)
    {
        string suffix = null;
        if (number == 0L)
        {
            return "0";
        }
        TryReduceUnit(ref number, ref suffix, 1073741824uL, "G", numberFormatOptions, NumberFormatOptions.Giga);
        TryReduceUnit(ref number, ref suffix, 1048576uL, "M", numberFormatOptions, NumberFormatOptions.Mega);
        TryReduceUnit(ref number, ref suffix, 1024uL, "K", numberFormatOptions, NumberFormatOptions.Kilo);
        if ((numberFormatOptions & NumberFormatOptions.Hex) != 0)
        {
            return $"0x{number:X}" + suffix;
        }
        return number + suffix;
    }

    public static string ReadNullTerminated(byte[] buffer, int startIndex, Encoding encoding)
    {
        int end;
        for (end = startIndex; end < buffer.Length && buffer[end] != 0; end++)
        {
        }
        return encoding.GetString(buffer, startIndex, end - startIndex);
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

    public static KeyValuePair<string, string> SplitAtLast(string text, char ch, string defaultKey = null, string defaultValue = null)
    {
        int index = text?.LastIndexOf(ch) ?? (-1);
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

    public static string FormatList(string header, IEnumerable items, string footer)
    {
        StringBuilder stringBuilder = new StringBuilder();
        if (!string.IsNullOrEmpty(header))
        {
            stringBuilder.Append(header);
        }
        if (items != null)
        {
            foreach (object item in items)
            {
                stringBuilder.Append("\r\n\t");
                stringBuilder.Append(item.ToString());
            }
        }
        if (!string.IsNullOrEmpty(footer))
        {
            stringBuilder.Append("\r\n");
            stringBuilder.Append(footer);
        }
        return stringBuilder.ToString();
    }

    public static string GetLeadingWhitespace(string text)
    {
        if (text == null)
        {
            return "";
        }
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\t' && text[i] != ' ')
            {
                return text.Substring(0, i);
            }
        }
        return text;
    }

    public static string JoinStrings(string separator, IEnumerable<string> items)
    {
        if (items == null)
        {
            return null;
        }
        return string.Join(separator, items.ToArray());
    }

    public static string JoinLines(IEnumerable<string> items, string prefix = "")
    {
        if (items == null)
        {
            return null;
        }
        return string.Join("\r\n", items.Select((string line) => prefix + line).ToArray());
    }

    public static string JoinNonEmpty(params string[] items)
    {
        return string.Join(" ", items.Where((string value) => !string.IsNullOrEmpty(value)).ToArray());
    }

    public static string JoinNonEmptyWith(string separator, params string[] items)
    {
        return string.Join(separator, items.Where((string value) => !string.IsNullOrEmpty(value)).ToArray());
    }

    public static List<string> SplitCommandLine(string text)
    {
        int tokenStart = 0;
        bool inQuotes = false;
        List<string> items = new List<string>();
        if (text == null)
        {
            return items;
        }
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (text[i] == ' ' && !inQuotes)
            {
                if (i != tokenStart)
                {
                    items.Add(text.Substring(tokenStart, i - tokenStart));
                }
                tokenStart = i + 1;
            }
        }
        if (tokenStart != text.Length)
        {
            items.Add(text.Substring(tokenStart));
        }
        return items;
    }

    public static string SplitCamelCase(string input)
    {
        string text = input;
        for (int i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]))
            {
                text = text.Insert(i++, " ");
                text = text.Substring(0, i) + char.ToLower(text[i]) + text.Substring(i + 1);
            }
        }
        return text;
    }

    public static string Pluralize(long count, string noun, bool isSizeFormat = false)
    {
        string formattedCount = (isSizeFormat ? FormatSize(count) : count.ToString());
        if (count == 1)
        {
            return formattedCount + " " + noun;
        }
        return formattedCount + " " + noun + "s";
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

    public static FirstTokenSplit SplitFirstToken(string text)
    {
        if (text == null)
        {
            return default;
        }
        text = text.Trim();
        if (text.StartsWith("\""))
        {
            int closingQuoteIndex = text.IndexOf("\"", 1);
            if (closingQuoteIndex == -1)
            {
                return new FirstTokenSplit
                {
                    First = text
                };
            }
            return new FirstTokenSplit
            {
                First = text.Substring(1, closingQuoteIndex - 1).Trim(),
                Rest = text.Substring(closingQuoteIndex + 1).Trim()
            };
        }
        int spaceIndex = text.IndexOf(" ");
        if (spaceIndex == -1)
        {
            return new FirstTokenSplit
            {
                First = text
            };
        }
        return new FirstTokenSplit
        {
            First = text.Substring(0, spaceIndex).Trim(),
            Rest = text.Substring(spaceIndex + 1).Trim()
        };
    }

    public static string QuoteIfNeeded(string text)
    {
        if (text == null)
        {
            return text;
        }
        if (text.IndexOf(' ') != -1 && !text.StartsWith("\""))
        {
            return "\"" + text + "\"";
        }
        return text;
    }

    public static string QuoteAndEscapeRegex(string text)
    {
        return "\"" + Regex.Escape(text) + "\"";
    }

    public static string UnquoteAndUnescape(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("\""))
        {
            return text;
        }
        text = text.Trim();
        if (text[0] != '"' || text[text.Length - 1] != '"')
        {
            return text;
        }
        return Regex.Unescape(text.Substring(1, text.Length - 2));
    }

    public static string SafeSubstring(this string text, int startIndex, int length)
    {
        if (text == null)
        {
            return null;
        }
        if (startIndex < 0 || startIndex > text.Length)
        {
            return null;
        }
        if (length < 0 || startIndex + length < 0 || startIndex + length > text.Length)
        {
            return null;
        }
        return text.Substring(startIndex, length);
    }
}
