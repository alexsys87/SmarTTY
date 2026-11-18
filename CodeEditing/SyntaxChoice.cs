namespace SmarTTY.CodeEditing;

/// <summary>
/// The syntax of a file in the remote file editor: the one the user chose (or none, then it is detected from the file name and the first line), and the one
/// the editor highlights with now. The state and the rules of <c>RemoteFileEditor.Syntax.cs</c>, moved here as they were; the events, the completion language
/// and the tokenizer stay in the editor.
/// </summary>
public sealed class SyntaxChoice
{
    /// <summary>The syntax chosen by the user for this file; null means "detect it from the name and the #! line".</summary>
    public SyntaxHighlighting.Language? Override { get; private set; }

    /// <summary>The syntax the editor highlights with now (Plain when highlighting is off).</summary>
    public SyntaxHighlighting.Language Current { get; private set; }

    /// <summary>The syntax chosen for the file, or the detected one when the choice is automatic.</summary>
    public SyntaxHighlighting.Language Detect(string remotePath, string firstLine)
    {
        return Override ?? SyntaxHighlighting.DetectLanguage(remotePath, firstLine);
    }

    /// <summary>Chooses the syntax by hand; null returns to the detection. False when that is the choice already.</summary>
    public bool Choose(SyntaxHighlighting.Language? language)
    {
        if (Override == language)
        {
            return false;
        }
        Override = language;
        return true;
    }

    /// <summary>The code that applies the tokenizer says which syntax it applied.</summary>
    public void Report(SyntaxHighlighting.Language language)
    {
        Current = language;
    }
}
