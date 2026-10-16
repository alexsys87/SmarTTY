using System.Text;
using System.Text.RegularExpressions;

namespace ManagedLibSSH2.QuickSync;

public static class WildcardParser
{
    public static Regex WildcardToRegex(string text2, bool flag2 = false, bool flag3 = false)
    {
        string text = "\\/[]().{}-+";
        for (int num = 0; num < text.Length; num++)
        {
            text2 = text2.Replace(text.Substring(num, 1), "\\" + text.Substring(num, 1));
        }
        text2 = text2.Replace("*", ".*");
        text2 = text2.Replace("?", ".");
        StringBuilder stringBuilder = new StringBuilder();
        if (flag3)
        {
            stringBuilder.Append("(^|.*/)(");
        }
        else
        {
            stringBuilder.Append("^(");
        }
        bool flag = true;
        string[] array = text2.Split(';');
        foreach (string value in array)
        {
            if (flag)
            {
                flag = false;
            }
            else
            {
                stringBuilder.Append("|");
            }
            stringBuilder.Append(value);
        }
        stringBuilder.Append(")$");
        if (flag2)
        {
            return new Regex(stringBuilder.ToString(), RegexOptions.IgnoreCase);
        }
        return new Regex(stringBuilder.ToString());
    }

    public static MaskSpec ParseMask(string text, bool flag2 = false)
    {
        if (string.IsNullOrEmpty(text) || text == "-" || text == "-\"\"")
        {
            return new MaskSpec(null, flag: false);
        }
        bool flag = true;
        if (text.StartsWith("-"))
        {
            flag = false;
            text = text.Substring(1);
        }
        if (text.Length >= 2 && text.StartsWith("\"") && text.EndsWith("\""))
        {
            text = text.Substring(1, text.Length - 2);
        }
        return new MaskSpec(WildcardParser.WildcardToRegex(text, flag2), flag);
    }
}
