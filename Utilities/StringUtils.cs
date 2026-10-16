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

    public delegate string PlaceholderResolver(string text, string text2);

    public struct FirstTokenSplit
    {
        public string First;

        public string Rest;
    }

    public static string AfterFirst(string text, char ch, string text2 = null)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.IndexOf(ch);
        if (num == -1)
        {
            return text2;
        }
        return text.Substring(num + 1);
    }

    public static IEnumerable<int> IndexesOf(string text, char ch)
    {
        int startIndex = 0;
        while (true)
        {
            int num = text.IndexOf(ch, startIndex);
            if (num != -1)
            {
                yield return num;
                startIndex = num + 1;
                continue;
            }
            break;
        }
    }

    public static string AfterLast(string text, char ch, string text2 = null)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.LastIndexOf(ch);
        if (num == -1)
        {
            return text2;
        }
        return text.Substring(num + 1);
    }

    public static string BeforeFirst(string text, char ch, string text2 = null)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.IndexOf(ch);
        if (num == -1)
        {
            return text2;
        }
        return text.Substring(0, num);
    }

    public static string BeforeLast(string text, char ch, string text2 = null)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.LastIndexOf(ch);
        if (num == -1)
        {
            return text2;
        }
        return text.Substring(0, num);
    }

    public static string FormatSize(long value2, bool flag = false)
    {
        string text = "";
        if (value2 > 10737418240L)
        {
            value2 /= 1073741824;
            text = "G";
        }
        if (value2 > 10485760)
        {
            value2 /= 1048576;
            text = "M";
        }
        else if (value2 > 10240)
        {
            value2 /= 1024;
            text = "K";
        }
        if (flag)
        {
            text = ((!(text == "")) ? (text + "B") : " bytes");
        }
        return value2 + text;
    }

    private static void TryReduceUnit(ref ulong value2, ref string text, ulong value3, string text2, NumberFormatOptions numberFormatOptions, NumberFormatOptions numberFormatOptions2)
    {
        if (string.IsNullOrEmpty(text2) && (numberFormatOptions & numberFormatOptions2) != NumberFormatOptions.None && value2 % value3 == 0L)
        {
            value2 /= value3;
            text = text2;
        }
    }

    public static string FormatNumber(ulong value2, NumberFormatOptions numberFormatOptions)
    {
        string text = null;
        if (value2 == 0L)
        {
            return "0";
        }
        TryReduceUnit(ref value2, ref text, 1073741824uL, "G", numberFormatOptions, NumberFormatOptions.Giga);
        TryReduceUnit(ref value2, ref text, 1048576uL, "M", numberFormatOptions, NumberFormatOptions.Mega);
        TryReduceUnit(ref value2, ref text, 1024uL, "K", numberFormatOptions, NumberFormatOptions.Kilo);
        if ((numberFormatOptions & NumberFormatOptions.Hex) != 0)
        {
            return $"0x{value2:X}" + text;
        }
        return value2 + text;
    }

    public static string ReadNullTerminated(byte[] buffer, int value2, Encoding encoding)
    {
        int num;
        for (num = value2; num < buffer.Length && buffer[num] != 0; num++)
        {
        }
        return encoding.GetString(buffer, value2, num - value2);
    }

    public static KeyValuePair<string, string> SplitAtFirst(string text, char ch, string text2 = null, string text3 = null)
    {
        int num = text?.IndexOf(ch) ?? (-1);
        if (num == -1)
        {
            return new KeyValuePair<string, string>(text2, text3);
        }
        return new KeyValuePair<string, string>(text.Substring(0, num), text.Substring(num + 1));
    }

    public static KeyValuePair<string, string> SplitAtLast(string text, char ch, string text2 = null, string text3 = null)
    {
        int num = text?.LastIndexOf(ch) ?? (-1);
        if (num == -1)
        {
            return new KeyValuePair<string, string>(text2, text3);
        }
        return new KeyValuePair<string, string>(text.Substring(0, num), text.Substring(num + 1));
    }

    public static string ExpandPlaceholders(string text3, PlaceholderResolver callback, char ch = ':')
    {
        if (text3 == null)
        {
            return null;
        }
        int num = 0;
        int num2 = -1;
        StringBuilder stringBuilder = new StringBuilder();
        for (int num3 = 0; num3 < text3.Length; num3++)
        {
            if (text3[num3] == '}' && num3 < text3.Length - 1 && text3[num3 + 1] == '}')
            {
                stringBuilder.Append(text3[num3]);
                num3++;
            }
            else if (text3[num3] == '{' && num3 < text3.Length - 1 && text3[num3 + 1] == '{')
            {
                stringBuilder.Append(text3[num3]);
                num3++;
            }
            else if (text3[num3] == '{')
            {
                if (num == 0)
                {
                    num2 = num3;
                }
                num++;
            }
            else if (text3[num3] == '}')
            {
                if (num <= 0)
                {
                    throw new Exception("Unexpected '}' in format string");
                }
                if (--num == 0)
                {
                    string text = text3.Substring(num2 + 1, num3 - num2 - 1);
                    int num4 = -2;
                    do
                    {
                        num4 = text.IndexOf(ch, num4 + 2);
                    }
                    while (num4 != -1 && num4 < text.Length - 2 && text[num4 + 1] == ch);
                    string text2 = null;
                    if (num4 != -1)
                    {
                        text2 = text.Substring(num4 + 1);
                        text = text.Substring(0, num4);
                    }
                    if (text.Contains("{"))
                    {
                        text = ExpandPlaceholders(text, callback, ch);
                    }
                    stringBuilder.Append(callback(text, text2));
                }
            }
            else if (num == 0)
            {
                stringBuilder.Append(text3[num3]);
            }
        }
        return stringBuilder.ToString();
    }

    public static int CommonPrefixLength(string text, string text2)
    {
        int num = Math.Min(text.Length, text2.Length);
        for (int num2 = 0; num2 < num; num2++)
        {
            if (char.ToLower(text[num2]) != char.ToLower(text2[num2]))
            {
                return num2;
            }
        }
        return num;
    }

    public static string FormatList(string text, IEnumerable items, string text2)
    {
        StringBuilder stringBuilder = new StringBuilder();
        if (!string.IsNullOrEmpty(text))
        {
            stringBuilder.Append(text);
        }
        if (items != null)
        {
            foreach (object item in items)
            {
                stringBuilder.Append("\r\n\t");
                stringBuilder.Append(item.ToString());
            }
        }
        if (!string.IsNullOrEmpty(text2))
        {
            stringBuilder.Append("\r\n");
            stringBuilder.Append(text2);
        }
        return stringBuilder.ToString();
    }

    public static string GetLeadingWhitespace(string text)
    {
        if (text == null)
        {
            return "";
        }
        for (int num = 0; num < text.Length; num++)
        {
            if (text[num] != '\t' && text[num] != ' ')
            {
                return text.Substring(0, num);
            }
        }
        return text;
    }

    public static string JoinStrings(string text, IEnumerable<string> items)
    {
        if (items == null)
        {
            return null;
        }
        return string.Join(text, items.ToArray());
    }

    public static string JoinLines(IEnumerable<string> items, string text2 = "")
    {
        if (items == null)
        {
            return null;
        }
        return string.Join("\r\n", items.Select((string text) => text2 + text).ToArray());
    }

    public static string JoinNonEmpty(params string[] items)
    {
        return string.Join(" ", items.Where((string value) => !string.IsNullOrEmpty(value)).ToArray());
    }

    public static string JoinNonEmptyWith(string text, params string[] items)
    {
        return string.Join(text, items.Where((string value) => !string.IsNullOrEmpty(value)).ToArray());
    }

    public static List<string> SplitCommandLine(string text)
    {
        int num = 0;
        bool flag = false;
        List<string> list = new List<string>();
        if (text == null)
        {
            return list;
        }
        for (int num2 = 0; num2 < text.Length; num2++)
        {
            if (text[num2] == '"')
            {
                flag = !flag;
            }
            else if (text[num2] == ' ' && !flag)
            {
                if (num2 != num)
                {
                    list.Add(text.Substring(num, num2 - num));
                }
                num = num2 + 1;
            }
        }
        if (num != text.Length)
        {
            list.Add(text.Substring(num));
        }
        return list;
    }

    public static string SplitCamelCase(string text2)
    {
        string text = text2;
        for (int num = 0; num < text.Length; num++)
        {
            if (num > 0 && char.IsUpper(text[num]))
            {
                text = text.Insert(num++, " ");
                text = text.Substring(0, num) + char.ToLower(text[num]) + text.Substring(num + 1);
            }
        }
        return text;
    }

    public static string Pluralize(long value2, string text2, bool flag = false)
    {
        string text = (flag ? FormatSize(value2) : value2.ToString());
        if (value2 == 1)
        {
            return text + " " + text2;
        }
        return text + " " + text2 + "s";
    }

    public static bool TryParseNumber(string text, out ulong value2)
    {
        ulong? num = ParseNumber(text);
        if (num.HasValue)
        {
            value2 = num.Value;
            return true;
        }
        value2 = 0uL;
        return false;
    }

    public static ulong? ParseNumber(string text)
    {
        if (text == null)
        {
            return null;
        }
        int num = text.IndexOf(' ');
        if (num != -1)
        {
            text = text.Substring(0, num);
        }
        ulong result;
        if (text.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase))
        {
            if (!ulong.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, null, out result))
            {
                return null;
            }
            return result;
        }
        if (!ulong.TryParse(text, out result))
        {
            return null;
        }
        return result;
    }

    public static ulong? ParseSize(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        ulong num = char.ToUpper(text[text.Length - 1]) switch
        {
            'K' => 1024uL,
            'M' => 1048576uL,
            'G' => 1073741824uL,
            _ => 1uL,
        };
        if (num != 1)
        {
            text = text.Substring(0, text.Length - 1);
        }
        return ParseNumber(text) * num;
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
            int num = text.IndexOf("\"", 1);
            if (num == -1)
            {
                return new FirstTokenSplit
                {
                    First = text
                };
            }
            return new FirstTokenSplit
            {
                First = text.Substring(1, num - 1).Trim(),
                Rest = text.Substring(num + 1).Trim()
            };
        }
        int num2 = text.IndexOf(" ");
        if (num2 == -1)
        {
            return new FirstTokenSplit
            {
                First = text
            };
        }
        return new FirstTokenSplit
        {
            First = text.Substring(0, num2).Trim(),
            Rest = text.Substring(num2 + 1).Trim()
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

    public static string SafeSubstring(this string text, int value2, int value3)
    {
        if (text == null)
        {
            return null;
        }
        if (value2 < 0 || value2 > text.Length)
        {
            return null;
        }
        if (value3 < 0 || value2 + value3 < 0 || value2 + value3 > text.Length)
        {
            return null;
        }
        return text.Substring(value2, value3);
    }
}
