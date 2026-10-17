using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Ssh.QuickSync;

public static class WildcardParser
{
    public static Regex WildcardToRegex(string pattern, bool ignoreCase = false, bool filenameOnly = false)
    {
        string escapedChars = "\\/[]().{}-+";
        for (int i = 0; i < escapedChars.Length; i++)
        {
            pattern = pattern.Replace(escapedChars.Substring(i, 1), "\\" + escapedChars.Substring(i, 1));
        }
        pattern = pattern.Replace("*", ".*");
        pattern = pattern.Replace("?", ".");
        StringBuilder stringBuilder = new StringBuilder();
        if (filenameOnly)
        {
            stringBuilder.Append("(^|.*/)(");
        }
        else
        {
            stringBuilder.Append("^(");
        }
        bool isFirst = true;
        string[] parts = pattern.Split(';');
        foreach (string part in parts)
        {
            if (isFirst)
            {
                isFirst = false;
            }
            else
            {
                stringBuilder.Append("|");
            }
            stringBuilder.Append(part);
        }
        stringBuilder.Append(")$");
        if (ignoreCase)
        {
            return new Regex(stringBuilder.ToString(), RegexOptions.IgnoreCase);
        }
        return new Regex(stringBuilder.ToString());
    }

    public static MaskSpec ParseMask(string maskText, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(maskText) || maskText == "-" || maskText == "-\"\"")
        {
            return new MaskSpec(null, flag: false);
        }
        bool isInclude = true;
        if (maskText.StartsWith("-"))
        {
            isInclude = false;
            maskText = maskText.Substring(1);
        }
        if (maskText.Length >= 2 && maskText.StartsWith("\"") && maskText.EndsWith("\""))
        {
            maskText = maskText.Substring(1, maskText.Length - 2);
        }
        return new MaskSpec(WildcardParser.WildcardToRegex(maskText, ignoreCase), isInclude);
    }
}
