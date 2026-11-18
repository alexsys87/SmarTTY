using SmarTTY.TextEditing;
using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// What the status bar of the remote file editor shows of the hex view, and the three view options its menu changes: what the bar asked of the
/// hex editor control. The control implements it.
/// </summary>
public interface IHexViewStatus
{
    /// <summary>The bytes cannot be changed.</summary>
    bool ReadOnly { get; }

    /// <summary>The offset of the caret.</summary>
    int CaretOffset { get; }

    /// <summary>The size of the file.</summary>
    int Length { get; }

    /// <summary>Typing adds bytes (Insert switches it); false is the overwrite mode.</summary>
    bool InsertMode { get; }

    /// <summary>How many bytes are selected.</summary>
    int SelectionLength { get; }

    bool BigEndian { get; set; }

    /// <summary>The bytes of a group of the layout: 1, 2, 4 or 8.</summary>
    int GroupSize { get; set; }

    int BytesPerRow { get; set; }

    bool ShowDataInspector { get; set; }

    bool ColorByValue { get; set; }
}

/// <summary>
/// What the status bar below the remote file editor reads of the editor and the commands of its menus: the file, whether it is saved, the caret, the
/// selection, the encoding, the line endings, the syntax and the hex view. It is what the bar asked of <c>RemoteFileEditor</c> (and of the panel for the
/// moment to read again), so that a bar of any other toolkit, or another editor, can be used with it. The adapter of the WinForms editor is
/// <c>Controls/RemoteFileEditorStatusSource.cs</c>.
/// </summary>
public interface IEditorStatusSource
{
    /// <summary>Something the bar shows may have changed: read it again.</summary>
    event EventHandler StateChanged;

    /// <summary>The syntax of the file changed (chosen by hand, or detected anew).</summary>
    event EventHandler CurrentLanguageChanged;

    /// <summary>The path of the file on the server.</summary>
    string RemotePath { get; }

    /// <summary>Only the beginning of a file that is too big is shown.</summary>
    bool IsPreview { get; }

    /// <summary>The user chose to read the file only, or has no right to write it.</summary>
    bool IsReadOnlyMode { get; }

    /// <summary>A background save is running.</summary>
    bool IsSaving { get; }

    bool IsModified { get; }

    /// <summary>The file did not exist when the editor was opened.</summary>
    bool IsNewFile { get; }

    /// <summary>The text cannot be changed (also while the file is loading).</summary>
    bool ReadOnly { get; }

    /// <summary>The file can be read again in another encoding.</summary>
    bool CanReopenWithEncoding { get; }

    /// <summary>The hex view is shown instead of the text.</summary>
    bool IsHexMode { get; }

    /// <summary>The hex view (what the bar shows while <see cref="IsHexMode"/>).</summary>
    IHexViewStatus HexView { get; }

    TextDocument.TextPosition CaretPosition { get; }

    bool HasSelection { get; }

    TextDocument.TextPosition SelectionStart { get; }

    TextDocument.TextPosition SelectionEnd { get; }

    /// <summary>The text of a line without its line break.</summary>
    string GetLineText(int line);

    /// <summary>The line break that ends a line.</summary>
    string GetEffectiveLineTerminator(int line);

    /// <summary>The text of the first line, or null while there is no text.</summary>
    string FirstLine { get; }

    TextFileEncoding FileEncoding { get; }

    LineEndingKind LineEndings { get; }

    /// <summary>The syntax in use.</summary>
    SyntaxHighlighting.Language CurrentLanguage { get; }

    /// <summary>The syntax chosen by hand, or null when it is detected.</summary>
    SyntaxHighlighting.Language? SyntaxOverride { get; }

    /// <summary>Saves the file in another encoding (the text stays as it is).</summary>
    void SetSaveEncoding(TextFileEncoding encoding);

    /// <summary>Reads the file again as <paramref name="encoding"/>; the changes are dropped (the caller asked first).</summary>
    void ReopenWithEncoding(TextFileEncoding encoding);

    /// <summary>Chooses the syntax by hand; null returns to the detection.</summary>
    void SetSyntax(SyntaxHighlighting.Language? language);

    /// <summary>Saves the file with other line endings (and converts the text).</summary>
    void SetLineEnding(TextDocument.LineEnding lineEnding);

    /// <summary>Gives the focus back to the view that is shown.</summary>
    void FocusActiveView();
}
