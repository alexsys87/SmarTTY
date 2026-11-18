using System.Text;

namespace SmarTTY.Terminal.Emulation;

/// <summary>The text of an OSC string (ESC ] ... BEL), the part between the introducer and the terminator.</summary>
public static class OscString
{
    /// <summary>
    /// Splits "command;text". Only the first ';' separates the command from its text: a title may contain ';' itself. False when there is no
    /// ';' or the command is not a number.
    /// </summary>
    public static bool TryParse(string content, out int command, out string text)
    {
        int separator = content.IndexOf(';');
        if (separator == -1 || !int.TryParse(content.Substring(0, separator), out command))
        {
            command = 0;
            text = null;
            return false;
        }
        text = content.Substring(separator + 1);
        return true;
    }

    /// <summary>The title that the text of OSC 0 or 2 sets: the text without its control characters.</summary>
    public static string TitleText(string text)
    {
        StringBuilder title = new StringBuilder();
        foreach (char ch in text)
        {
            if (!char.IsControl(ch))
            {
                title.Append(ch);
            }
        }
        return title.ToString();
    }
}
