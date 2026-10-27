namespace SmarTTY.Utilities;

/// <summary>
/// The small rules of the snippet editor (<c>GUI/SnippetEditWindow</c>): how the text box normalises line breaks,
/// how the name is stored and when a snippet may be saved. They carry no UI dependency, so the test harness can
/// check them without a window.
/// </summary>
public static class SnippetRules
{
    /// <summary>
    /// The text as the editor box holds it: the line breaks are normalised to CRLF, as the WinForms editor did, so a
    /// pasted text and a typed one look the same.
    /// </summary>
    public static string NormalizeText(string text)
    {
        return (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    /// <summary>
    /// The name to store. An empty or blank name is kept empty on purpose: the paste menu then shows the start of the
    /// text instead of a name.
    /// </summary>
    public static string TrimName(string name)
    {
        return (name ?? "").Trim();
    }

    /// <summary>
    /// True when the edited text may be saved. A snippet without text is refused, so the Save button keeps the window
    /// open and moves the caret to the text box.
    /// </summary>
    public static bool IsAcceptable(string text)
    {
        return !string.IsNullOrEmpty(text);
    }
}
