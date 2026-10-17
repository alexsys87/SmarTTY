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
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\u001b')
            {
                stringBuilder.Append(text[i]);
            }
            else if (i != text.Length - 1)
            {
                i += GetSequenceLength(text, i) - 1;
            }
        }
        return stringBuilder.ToString();
    }

    public static int FindCsiEnd(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] >= '@' && text[i] <= '~')
            {
                return i;
            }
        }
        return -1;
    }

    public static int FindOscEnd(string text, int startIndex, out int sequenceLength)
    {
        sequenceLength = 0;
        for (int i = startIndex; i < text.Length; i++)
        {
            char currentChar = text[i];
            if (currentChar == '\a')
            {
                sequenceLength = i - startIndex + 1;
                return i;
            }
            if (i < text.Length - 1 && currentChar == '\u001b' && text[i + 1] == '\\')
            {
                sequenceLength = i - startIndex + 2;
                return i;
            }
        }
        return -1;
    }

    private static int GetSequenceLength(string text, int startIndex)
    {
        switch (text[startIndex + 1])
        {
            case ']':
                {
                    int endIndex = FindOscEnd(text, startIndex + 1, out var oscLength);
                    if (endIndex == -1)
                    {
                        return 2;
                    }
                    return oscLength + 1;
                }
            case '[':
                {
                    int endIndex = FindCsiEnd(text, startIndex + 2);
                    if (endIndex == -1)
                    {
                        return 2;
                    }
                    return endIndex - startIndex + 1;
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
