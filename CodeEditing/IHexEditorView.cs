using SmarTTY.TextEditing;
using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The hex view of the remote file editor as the rest of the application uses it: the editor that switches to it (<see cref="IRemoteHexView"/>),
/// the status bar (<see cref="IHexViewStatus"/>), the panel with its commands and context menu, the menu of the hex view (<see cref="HexMenuModel"/>),
/// the find window, the dialogs of the tab and the main window (the settings of the view, F3, Go to Offset). It is made of the calls those made on
/// <c>HexEditorControl</c> (stage 3B-3), so that the WPF view (<c>GUI/HexView/WpfHexEditor</c>) can take its place; both implement it.
/// </summary>
public interface IHexEditorView : IRemoteHexView, IHexViewStatus
{
    /// <summary>The bytes cannot be edited (one property for both interfaces).</summary>
    new bool ReadOnly { get; set; }

    /// <summary>The caret, the selection, the bytes, the typing mode or a view option changed.</summary>
    event EventHandler StateChanged;

    /// <summary>Ctrl+S was pressed in the view.</summary>
    event EventHandler SaveRequested;

    /// <summary>Ctrl+F was pressed in the view.</summary>
    event EventHandler FindRequested;

    /// <summary>Ctrl+G was pressed in the view.</summary>
    event EventHandler GoToOffsetRequested;

    /// <summary>The byte order, the groups, the row width, the data inspector or the colours by value changed.</summary>
    event EventHandler ViewOptionsChanged;

    /// <summary>The view has the keyboard focus.</summary>
    bool Focused { get; }

    bool HasSelection { get; }

    /// <summary>The first selected byte (the caret without a selection).</summary>
    int SelectionStart { get; }

    bool CanUndo { get; }

    bool CanRedo { get; }

    /// <summary>The hexadecimal text of the last search (what F3 looks for), or empty.</summary>
    string LastSearchText { get; }

    /// <summary>The coloured block under the caret, or null.</summary>
    HexHighlight HighlightAtCaret { get; }

    bool HasHighlights { get; }

    /// <summary>There is a selection or a byte under the caret to mark.</summary>
    bool CanHighlight { get; }

    void Undo();

    void Redo();

    bool CopySelection();

    void CopyAll();

    void Cut();

    void Paste();

    void DeleteSelection();

    void SelectAll();

    /// <summary>Copies the selection (the byte under the caret without one) as text of <paramref name="format"/>.</summary>
    bool CopyAs(HexCopyFormat format);

    /// <summary>The bytes the checksums are made of (the selection, or the whole file) and what they are.</summary>
    byte[] GetChecksumBytes(out string description);

    bool FillSelection(byte[] pattern);

    bool InsertPattern(int count, byte[] pattern);

    bool SwapSelectionByteOrder(int size, out string error);

    /// <summary>Marks the selected bytes (the byte under the caret without a selection) with a colour (made opaque) and a label.</summary>
    bool HighlightSelection(RgbColor color, string label);

    bool RemoveHighlight();

    void ClearHighlights();

    bool GoToHighlight(bool forward);

    void GoToOffset(int offset);

    bool Search(byte[] pattern, bool forward, bool ignoreCase, bool wrap);

    /// <summary>Repeats the last search (F3); a beep when nothing is found, <see cref="FindRequested"/> when there was no search yet.</summary>
    bool FindNext(bool forward);

    /// <summary>Sets the view options from the settings.</summary>
    void LoadViewOptions(int bytesPerRow, int groupSize, bool bigEndian, bool colorByValue, bool showDataInspector);
}
