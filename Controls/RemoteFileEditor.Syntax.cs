using SmarTTY.CodeEditing;
using System;

namespace SmarTTY.Controls;

public partial class RemoteFileEditor
{
    /// <summary>The syntax chosen by the user for this file; null means "detect it from the name and the #! line".</summary>
    public SyntaxHighlighting.Language? SyntaxOverride { get; private set; }

    /// <summary>The syntax the editor highlights with now (Plain when highlighting is off).</summary>
    public SyntaxHighlighting.Language CurrentLanguage { get; private set; }

    /// <summary>The tokenizer was applied: <see cref="CurrentLanguage"/> changed or was confirmed.</summary>
    public event EventHandler CurrentLanguageChanged;

    /// <summary>The user chose another syntax, or went back to automatic detection.</summary>
    public event EventHandler SyntaxChoiceChanged;

    /// <summary>The syntax chosen for the file, or the detected one when the choice is automatic.</summary>
    public SyntaxHighlighting.Language DetectSyntax()
    {
        return SyntaxOverride ?? SyntaxHighlighting.DetectLanguage(RemotePath, EditableDocument?.GetLineText(0));
    }

    /// <summary>Comments follow the syntax of the file, also when the highlighting is switched off.</summary>
    public override CommentStyle CommentStyle => CommentStyle.ForLanguage(DetectSyntax());

    /// <summary>Chooses the syntax by hand; null returns to the detection by the file name and the #! line.</summary>
    public void SetSyntax(SyntaxHighlighting.Language? language)
    {
        if (SyntaxOverride == language)
        {
            return;
        }
        SyntaxOverride = language;
        SyntaxChoiceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the code that applies the tokenizer, so that the status bar can show the result.</summary>
    internal void ReportLanguage(SyntaxHighlighting.Language language)
    {
        CurrentLanguage = language;
        // The completion popup offers the words of the file's language (chosen by hand, or detected from the
        // name and the #! line), whether or not the highlighting is switched on.
        CompletionLanguage = DetectSyntax();
        CurrentLanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
