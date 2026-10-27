using System.Collections.Generic;

namespace SmarTTY.Utilities;

/// <summary>
/// The small rules of the paste confirmation (<c>GUI/PasteConfirmWindow</c>): the window caption, the warning line and
/// whether Enter may paste at once. They carry no UI dependency, so the test harness can check them without a window.
/// </summary>
public static class PasteConfirmRules
{
    /// <summary>The caption: a paste that holds dangerous commands says so.</summary>
    public static string Title(bool dangerous)
    {
        return dangerous ? "Paste - dangerous commands" : "Paste into the terminal";
    }

    /// <summary>
    /// The warning line that names what is dangerous in the text, or an empty string when nothing is. The line is
    /// shown in the warning colour, so the caller only has to place it.
    /// </summary>
    public static string DangerLine(IList<string> dangers)
    {
        if (dangers == null || dangers.Count == 0)
        {
            return "";
        }
        return "Warning: the text " + string.Join("; ", dangers) + ".";
    }

    /// <summary>
    /// True when the safe answer (Cancel) must be the one Enter reaches, so dangerous text is not pasted by accident.
    /// </summary>
    public static bool DefaultToCancel(bool dangerous)
    {
        return dangerous;
    }
}
