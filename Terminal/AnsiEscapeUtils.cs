namespace SmarTTY.Terminal;

public static class AnsiEscapeUtils
{
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
}
