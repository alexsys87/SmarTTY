using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The text view of a <see cref="RemoteFileEditor"/>: the calls the editor made on its base class, <c>CodeEditorPP</c>, when it was one (stage 3E-4).
/// The WPF view of the editor tab implements it (the WinForms one, <c>RemoteTextView</c>, went with the WinForms editor in stage 3E-5).
/// </summary>
public interface IRemoteTextView
{
    /// <summary>The text.</summary>
    EditableTextDocument Document { get; }

    /// <summary>The modified state of the text itself (the editor's own covers the hex view too).</summary>
    bool IsTextModified { get; set; }

    bool ReadOnly { get; set; }

    TextDocument.TextPosition CaretPosition { get; }

    bool HasSelection { get; }

    /// <summary>Where the selection starts; it ends at <see cref="SelectionEnd"/>.</summary>
    TextDocument.TextPosition SelectionStart { get; }

    TextDocument.TextPosition SelectionEnd { get; }

    void SetCaret(TextDocument.TextPosition position);

    /// <summary>The version of the text in the undo history (compared with the one a save took).</summary>
    object CurrentVersion { get; }

    void SetDocument(EditableTextDocument document);

    void MarkSaved(object version);

    /// <summary>Tells the view's listeners that the modified state changed (the hex view changed it).</summary>
    void RaiseModifiedChanged();

    /// <summary>Puts the keyboard focus on the text.</summary>
    void FocusText();

    bool WordWrap { get; set; }

    /// <summary>The marks of the blanks are shown.</summary>
    bool ShowAllCharacters { get; set; }

    int LineCount { get; }

    // What the commands of the tab (RemoteFileEditorCommands) do with the text and read of it.
    bool CanUndo { get; }

    bool CanRedo { get; }

    void Undo();

    void Redo();

    /// <summary>More than one line is selected (the Text menu sorts and removes duplicates only then).</summary>
    bool HasLineSelection { get; }

    /// <summary>Runs a command of the Text menu on the selected lines (or all of them); false when nothing changed.</summary>
    bool ApplyLineTransform(LineTransformKind kind);

    /// <summary>Moves the caret to a line and a column counted from 0 and shows it.</summary>
    void GoToPosition(int line, int column);

    void ClearMessages();

    void AddMessages(IEnumerable<InlineMessageProvider.MessageRequest> messages);

    void ApplyMessageColors(RgbColor background, RgbColor error, RgbColor warning, RgbColor border);

    FoldedLineInfo[] GetFoldState();

    void RestoreFoldState(FoldedLineInfo[] items);

    /// <summary>The language of the words the completion offers.</summary>
    SyntaxHighlighting.Language CompletionLanguage { get; set; }

    // The commands of the text itself (the editor sends them to the hex view while that is shown).
    void CopySelection(bool select);

    void CopyAll();

    void Cut();

    void Paste();

    void DeleteSelection();

    void SelectAllText();

    /// <summary>The colours and the font of the text as a hex view takes them: the family and the size in points at the zoom of the text.</summary>
    void GetAppearance(out RgbColor background, out RgbColor foreground, out string fontFamily, out float fontSizeInPoints);

    /// <summary>The colours, the font or the zoom of the text changed.</summary>
    event EventHandler AppearanceChanged;

    /// <summary>The view was closed (disposed): the loading and the drafts of the editor stop.</summary>
    bool IsClosed { get; }

    event EventHandler Closed;

    /// <summary>Closes (disposes) the view.</summary>
    void Close();

    /// <summary>Runs <paramref name="action"/> later on the thread of the view; false when the view is closed and cannot take it.</summary>
    bool Post(Action action);

    /// <summary>The thread of the view, for the loading of the file.</summary>
    IUiDispatcher UiDispatcher { get; }

    /// <summary>The questions the loading asks from its thread.</summary>
    IUserPrompt UserPrompt { get; }

    /// <summary>The window the message boxes of the editor belong to (null: the main window).</summary>
    IWin32Window MessageOwner { get; }
}
