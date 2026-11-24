using SmarTTY.TextEditing;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The remote file editor as <see cref="RemoteFileContent"/> sees it: its text view, the place where the hex view is shown, the state of the
/// file, and the two messages of the switching between the views. Made of the calls the switching made on the editor (<c>RemoteFileEditor</c>,
/// which implements it explicitly).
/// </summary>
public interface IRemoteFileContentHost
{
    /// <summary>The document of the text view.</summary>
    EditableTextDocument TextDocument { get; }

    /// <summary>The text has unsaved changes (the state of the text view alone, not of the hex view).</summary>
    bool IsTextModified { get; set; }

    /// <summary>The text cannot be edited.</summary>
    bool TextReadOnly { get; set; }

    /// <summary>The caret of the text view.</summary>
    TextDocument.TextPosition Caret { get; }

    /// <summary>Puts the caret of the text view at <paramref name="position"/>.</summary>
    void SetCaret(TextDocument.TextPosition position);

    /// <summary>Shows <paramref name="document"/> in the text view, in place of the document it had.</summary>
    void SetDocument(EditableTextDocument document);

    /// <summary>Puts the keyboard focus on the text view.</summary>
    void FocusText();

    /// <summary>Records that the text of <paramref name="version"/> has been saved.</summary>
    void MarkTextSaved(object version);

    /// <summary>Tells that the modified state of the editor may have changed (its <c>ModifiedChanged</c> event).</summary>
    void RaiseModifiedChanged();

    /// <summary>The whole file has been loaded.</summary>
    bool IsLoaded { get; }

    /// <summary>A save runs in the background.</summary>
    bool IsSaving { get; }

    /// <summary>The user chose to only read the file.</summary>
    bool IsReadOnlyMode { get; }

    /// <summary>Shows the hex view in front of the text view.</summary>
    void ShowHexView();

    /// <summary>Hides the hex view.</summary>
    void HideHexView();

    /// <summary>Shows an error (the text has no bytes in the encoding of the file).</summary>
    void ShowError(string message);

    /// <summary>Asks a question with a warning; true when the answer is Yes.</summary>
    bool ConfirmWarning(string question);
}

/// <summary>
/// The hex view as <see cref="RemoteFileContent"/> sees it: made of the calls the switching made on <c>HexEditorControl</c>, which has them.
/// </summary>
public interface IRemoteHexView
{
    /// <summary>The bytes the view shows and edits; null shows an empty document.</summary>
    HexDocument Document { get; set; }

    /// <summary>The bytes cannot be edited.</summary>
    bool ReadOnly { get; set; }

    /// <summary>Puts the keyboard focus on the view.</summary>
    bool Focus();

    /// <summary>Draws the view again.</summary>
    void Invalidate();
}
