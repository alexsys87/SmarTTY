using System.Text;

namespace SmarTTY;

public static class AnsiEscapeUtils
{
    public static class EscapeChars
    {
        public const char Escape = '\u001b';

        public const char Bell = '\a';
    }

    public static string StripEscapeSequences(string text)
    {
        if (text.IndexOf('\u001b') == -1)
        {
            return text;
        }
        StringBuilder stringBuilder = new StringBuilder();
        for (int num = 0; num < text.Length; num++)
        {
            if (text[num] != '\u001b')
            {
                stringBuilder.Append(text[num]);
            }
            else if (num != text.Length - 1)
            {
                num += GetSequenceLength(text, num) - 1;
            }
        }
        return stringBuilder.ToString();
    }

    public static int FindCsiEnd(string text, int value2)
    {
        for (int num = value2; num < text.Length; num++)
        {
            if (text[num] >= '@' && text[num] <= '~')
            {
                return num;
            }
        }
        return -1;
    }

    public static int FindOscEnd(string text, int value2, out int value3)
    {
        value3 = 0;
        for (int num = value2; num < text.Length; num++)
        {
            char ch = text[num];
            if (ch == '\a')
            {
                value3 = num - value2 + 1;
                return num;
            }
            if (num < text.Length - 1 && ch == '\u001b' && text[num + 1] == '\\')
            {
                value3 = num - value2 + 2;
                return num;
            }
        }
        return -1;
    }

    private static int GetSequenceLength(string text, int value2)
    {
        switch (text[value2 + 1])
        {
            case ']':
                {
                    int num = FindOscEnd(text, value2 + 1, out var num2);
                    if (num == -1)
                    {
                        return 2;
                    }
                    return num2 + 1;
                }
            case '[':
                {
                    int num = FindCsiEnd(text, value2 + 2);
                    if (num == -1)
                    {
                        return 2;
                    }
                    return num - value2 + 1;
                }
            case ' ':
            case '#':
            case '%':
            case '(':
            case ')':
            case '*':
            case '+':
                return 3;
            default:
                return 2;
        }
    }
}
