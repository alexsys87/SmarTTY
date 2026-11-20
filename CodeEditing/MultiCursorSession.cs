using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY.CodeEditing;

/// <summary>What the cursors of a code editor read of its view and ask of it (the calls <c>CodeEditorPP.MultiCursor</c> made on itself).</summary>
public interface IMultiCursorHost
{
    EditableTextDocument Document { get; }

    TextDocument.TextPosition CaretPosition { get; }

    bool HasSelection { get; }

    TextDocument.TextPosition SelectionStart { get; }

    /// <summary>The cursors can change the text: an editable view that is not read-only, with a document.</summary>
    bool CanUseCursors { get; }

    bool InsertSpaces { get; }

    int TabSize { get; }

    void SetSelection(bool select, TextDocument.TextPosition anchor);

    void SetCaret(TextDocument.TextPosition position);

    void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text);

    void ExecuteAsSingleUndoStep(Action edits);

    void UpdateMaxLineWidth(int line);

    /// <summary>Closes the word list of the completion.</summary>
    void HideCompletion();

    /// <summary>Draws the view again (the other cursors are drawn by it).</summary>
    void Redraw();
}

/// <summary>What a key did to the cursors.</summary>
public enum CursorKeyResult
{
    /// <summary>The view handles the key as usual.</summary>
    NotHandled,

    Handled,

    /// <summary>Handled, and the character the key would type must not reach the text (Ctrl+Backspace).</summary>
    HandledSuppressKeyPress
}

/// <summary>
/// The other cursors of a code editor besides the caret: the set they make with the caret and the selection, the commands that make them
/// (Ctrl+D, Ctrl+Shift+L, Ctrl+Alt+Up/Down, Alt+click), what typing, the keys, copy, cut and paste do with them, the edits of a plan made as one undo
/// step, and the rule that anything that knows of one cursor only (an edit, a caret move of another kind, a command for one cursor) goes back to one.
/// Moved as it was from <c>CodeEditorPP.MultiCursor</c> (stage 3E), so that the WPF text view has the same cursors; the operations on the set are
/// <see cref="MultiCursor"/>, the views draw the cursors.
/// </summary>
public sealed class MultiCursorSession
{
    private readonly IMultiCursorHost _host;

    // The cursors other than the main one (the caret and the selection of the view).
    private readonly List<TextCursor> _extraCursors = new List<TextCursor>();

    // Set while the cursors change the text or the caret, so that the change does not take them away.
    private bool _applyingMultiCursor;

    // Ctrl+D started on a word under the caret: the next occurrences are whole words.
    private bool _wholeWordOccurrences;

    // The column the main cursor goes back to when it moves up and down.
    private int _primaryGoal = -1;

    // Where Alt+Shift+click starts its column of cursors: the last click or Alt+click.
    private TextPoint? _columnAnchor;

    private sealed class DocumentText : IMultiCursorText
    {
        private readonly EditableTextDocument _document;

        public DocumentText(EditableTextDocument document)
        {
            _document = document;
        }

        public int LineCount => _document.LineCount;

        public string GetLine(int line) => _document.GetLineText(line);
    }

    public MultiCursorSession(IMultiCursorHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public bool HasMultipleCursors => _extraCursors.Count > 0;

    /// <summary>The main cursor and the others.</summary>
    public int CursorCount => 1 + _extraCursors.Count;

    /// <summary>The cursors other than the main one (the views draw them).</summary>
    public IReadOnlyList<TextCursor> ExtraCursors => _extraCursors;

    /// <summary>The cursors are changing the text or the caret (a change then is theirs and leaves them).</summary>
    public bool IsApplying => _applyingMultiCursor;

    /// <summary>The commands of the editor that work with several cursors; the others are for one and take the extra cursors away first.</summary>
    public static bool IsMultiCursorCommand(string commandName)
    {
        switch (commandName)
        {
            case "AddNextOccurrence":
            case "SelectAllOccurrences":
            case "AddCursorAbove":
            case "AddCursorBelow":
                return true;
            default:
                return false;
        }
    }

    private IMultiCursorText CursorText => new DocumentText(_host.Document);

    private static TextPoint ToPoint(TextDocument.TextPosition position) => new TextPoint(position.Line, position.Column);

    private static TextDocument.TextPosition ToPosition(TextPoint point) => new TextDocument.TextPosition(point.Column, point.Line);

    private CursorSet BuildCursorSet()
    {
        TextPoint caret = ToPoint(_host.CaretPosition);
        TextPoint anchor = _host.HasSelection ? ToPoint(_host.SelectionStart) : caret;
        List<TextCursor> all = new List<TextCursor>(_extraCursors) { new TextCursor(anchor, caret, _primaryGoal) };
        return CursorSet.Create(all, all.Count - 1, _wholeWordOccurrences);
    }

    // Makes the cursors of the set the cursors of the view: the primary one becomes the caret and the selection.
    private void ShowCursorSet(CursorSet set)
    {
        bool wasApplying = _applyingMultiCursor;
        _applyingMultiCursor = true;
        try
        {
            TextCursor primary = set.PrimaryCursor;
            _extraCursors.Clear();
            for (int i = 0; i < set.Count; i++)
            {
                if (i != set.Primary)
                {
                    _extraCursors.Add(set.Items[i]);
                }
            }
            _wholeWordOccurrences = set.WholeWord;
            _primaryGoal = primary.Goal;
            _host.SetSelection(primary.HasSelection, ToPosition(primary.Anchor));
            _host.SetCaret(ToPosition(primary.Caret));
        }
        finally
        {
            _applyingMultiCursor = wasApplying;
        }
        _host.Redraw();
    }

    /// <summary>Goes back to a single cursor (the main one stays).</summary>
    public void ClearExtraCursors()
    {
        if (_extraCursors.Count == 0)
        {
            return;
        }
        _extraCursors.Clear();
        _wholeWordOccurrences = false;
        _primaryGoal = -1;
        _host.Redraw();
    }

    /// <summary>The text, the caret or the selection changed: a change that is not the cursors' own takes the extra cursors away.</summary>
    public void OnViewChanged()
    {
        if (!_applyingMultiCursor)
        {
            ClearExtraCursors();
        }
    }

    /// <summary>A click with the left button put the caret: an Alt+Shift+click makes its column of cursors from there.</summary>
    public void OnCaretClicked(TextDocument.TextPosition caret)
    {
        _columnAnchor = new TextPoint(caret.Line, caret.Column);
    }

    /// <summary>Ctrl+D: selects the word under the caret; with a selection, adds a cursor on its next occurrence.</summary>
    public void AddNextOccurrence()
    {
        ChangeCursors((IMultiCursorText text, CursorSet set) => MultiCursor.AddNextOccurrence(text, set));
    }

    /// <summary>Ctrl+Shift+L: a cursor on every occurrence of the selected text (or of the word under the caret).</summary>
    public void SelectAllOccurrences()
    {
        ChangeCursors((IMultiCursorText text, CursorSet set) => MultiCursor.SelectAllOccurrences(text, set));
    }

    public void AddCursorAbove()
    {
        ChangeCursors((IMultiCursorText text, CursorSet set) => MultiCursor.AddOnAdjacentLine(text, set, below: false));
    }

    public void AddCursorBelow()
    {
        ChangeCursors((IMultiCursorText text, CursorSet set) => MultiCursor.AddOnAdjacentLine(text, set, below: true));
    }

    private void ChangeCursors(Func<IMultiCursorText, CursorSet, CursorSet> change)
    {
        if (!_host.CanUseCursors)
        {
            return;
        }
        CursorSet before = BuildCursorSet();
        CursorSet after = change(CursorText, before);
        if (!ReferenceEquals(before, after))
        {
            _host.HideCompletion();
            ShowCursorSet(after);
        }
    }

    // Makes the edits of the plan in the document (last first, so that the places of the earlier ones hold) as one undo step.
    private void ApplyCursorPlan(EditPlan plan)
    {
        if (!_host.CanUseCursors)
        {
            return;
        }
        bool wasApplying = _applyingMultiCursor;
        _applyingMultiCursor = true;
        try
        {
            _host.ExecuteAsSingleUndoStep(() =>
            {
                for (int i = plan.Edits.Count - 1; i >= 0; i--)
                {
                    TextEdit edit = plan.Edits[i];
                    if (edit.Start == edit.End && edit.Text.Length == 0)
                    {
                        continue;
                    }
                    _host.ReplaceText(ToPosition(edit.Start), ToPosition(edit.End), select: false, edit.Text);
                }
            });
            ShowCursorSet(ClampToText(plan.Result));
            _host.UpdateMaxLineWidth(-1);
        }
        finally
        {
            _applyingMultiCursor = wasApplying;
        }
    }

    // The editor can change the text a little differently from the plan (it never should); a cursor must not end up outside the text.
    private CursorSet ClampToText(CursorSet set)
    {
        IMultiCursorText text = CursorText;
        bool changed = false;
        TextCursor[] items = (TextCursor[])set.Items.Clone();
        for (int i = 0; i < items.Length; i++)
        {
            TextPoint caret = ClampPoint(text, items[i].Caret);
            TextPoint anchor = ClampPoint(text, items[i].Anchor);
            if (caret != items[i].Caret || anchor != items[i].Anchor)
            {
                items[i] = new TextCursor(anchor, caret);
                changed = true;
            }
        }
        return changed ? CursorSet.Create(items, set.Primary) : set;
    }

    private static TextPoint ClampPoint(IMultiCursorText text, TextPoint point)
    {
        int line = Math.Max(0, Math.Min(point.Line, text.LineCount - 1));
        return new TextPoint(line, Math.Max(0, Math.Min(point.Column, text.GetLine(line).Length)));
    }

    private string CursorLineBreak()
    {
        string lineBreak = _host.Document.GetEffectiveLineTerminator(_host.CaretPosition.Line);
        return string.IsNullOrEmpty(lineBreak) ? "\n" : lineBreak;
    }

    /// <summary>
    /// A character typed with several cursors (the KeyPress of the view; <paramref name="shift"/>: Shift is held); true when it was dealt with,
    /// also for a control character, which types nothing (the commands of Ctrl+letter are keys).
    /// </summary>
    public bool TypeCharacter(char keyChar, bool shift)
    {
        if (!HasMultipleCursors || !_host.CanUseCursors)
        {
            return false;
        }
        IMultiCursorText text = CursorText;
        CursorSet set = BuildCursorSet();
        EditPlan plan;
        switch (keyChar)
        {
            case '\r':
                plan = MultiCursor.NewLine(text, set, CursorLineBreak());
                break;
            case '\b':
                plan = MultiCursor.Backspace(text, set, _host.InsertSpaces ? Math.Max(1, _host.TabSize) : 0);
                break;
            case '\t':
                if (shift)
                {
                    // Shift+Tab takes the indentation of the lines away: that is for one cursor.
                    ClearExtraCursors();
                    return false;
                }
                plan = MultiCursor.Tab(text, set, _host.TabSize, _host.InsertSpaces);
                break;
            default:
                if (char.IsControl(keyChar))
                {
                    // Ctrl+letter: the commands are dealt with on the key.
                    return true;
                }
                plan = MultiCursor.Insert(text, set, new string(keyChar, 1));
                break;
        }
        _host.HideCompletion();
        ApplyCursorPlan(plan);
        return true;
    }

    /// <summary>The keys that move the cursors and delete with several cursors (the KeyDown of the view, its key and the modifiers held).</summary>
    public CursorKeyResult KeyDown(VirtualKey key, bool control, bool shift, bool alt)
    {
        if (!HasMultipleCursors || !_host.CanUseCursors)
        {
            return CursorKeyResult.NotHandled;
        }
        bool ctrl = control && !alt;
        bool plain = !control && !alt;
        IMultiCursorText text = CursorText;
        if (key == VirtualKey.Escape)
        {
            ClearExtraCursors();
            _host.HideCompletion();
            return CursorKeyResult.Handled;
        }
        if (key == VirtualKey.PageUp || key == VirtualKey.PageDown
            || (ctrl && (key == VirtualKey.A || key == VirtualKey.Z || key == VirtualKey.Y || key == VirtualKey.Home || key == VirtualKey.End)))
        {
            // These work on the whole text or on the view: one cursor, then the usual command.
            ClearExtraCursors();
            return CursorKeyResult.NotHandled;
        }
        CursorMove? move = null;
        switch (key)
        {
            case VirtualKey.Left:
                move = ctrl ? CursorMove.WordLeft : (plain ? CursorMove.Left : (CursorMove?)null);
                break;
            case VirtualKey.Right:
                move = ctrl ? CursorMove.WordRight : (plain ? CursorMove.Right : (CursorMove?)null);
                break;
            case VirtualKey.Up:
                move = plain ? CursorMove.Up : (CursorMove?)null;
                break;
            case VirtualKey.Down:
                move = plain ? CursorMove.Down : (CursorMove?)null;
                break;
            case VirtualKey.Home:
                move = plain ? CursorMove.Home : (CursorMove?)null;
                break;
            case VirtualKey.End:
                move = plain ? CursorMove.End : (CursorMove?)null;
                break;
            case VirtualKey.Back:
                if (ctrl && !shift)
                {
                    _host.HideCompletion();
                    ApplyCursorPlan(MultiCursor.DeleteWord(text, BuildCursorSet(), forward: false));
                    // Ctrl+Backspace types a control character that must not reach the text.
                    return CursorKeyResult.HandledSuppressKeyPress;
                }
                return CursorKeyResult.NotHandled;
            case VirtualKey.Delete:
                if (!shift && !alt)
                {
                    _host.HideCompletion();
                    CursorSet set = BuildCursorSet();
                    ApplyCursorPlan(control ? MultiCursor.DeleteWord(text, set, forward: true) : MultiCursor.Delete(text, set));
                    return CursorKeyResult.Handled;
                }
                return CursorKeyResult.NotHandled;
        }
        if (move.HasValue)
        {
            _host.HideCompletion();
            ShowCursorSet(MultiCursor.Move(text, BuildCursorSet(), move.Value, shift));
            return CursorKeyResult.Handled;
        }
        return CursorKeyResult.NotHandled;
    }

    /// <summary>The selected texts of several cursors as the clipboard takes them: one under the other, with CR LF line breaks.</summary>
    public static string ForClipboard(IEnumerable<string> texts)
    {
        return string.Join("\r\n", texts.Select(item => item.Replace("\r\n", "\n").Replace("\n", "\r\n")));
    }

    /// <summary>With several cursors: the selected texts, one under the other.</summary>
    public bool TryCopyCursors(out List<string> texts)
    {
        texts = null;
        if (!HasMultipleCursors)
        {
            return false;
        }
        texts = MultiCursor.SelectedTexts(CursorText, BuildCursorSet());
        return texts.Count > 0;
    }

    /// <summary>
    /// Cut with several cursors: their texts go to the clipboard (<paramref name="copyToClipboard"/>, false when it did not take them) and out of
    /// the text. False when there is nothing for several cursors to cut (the view cuts as usual).
    /// </summary>
    public bool TryCut(Func<string, bool> copyToClipboard)
    {
        if (_host.CanUseCursors && TryCopyCursors(out List<string> texts))
        {
            if (copyToClipboard(ForClipboard(texts)))
            {
                ApplyCursorPlan(MultiCursor.DeleteSelections(CursorText, BuildCursorSet()));
            }
            return true;
        }
        return false;
    }

    /// <summary>A paste with several cursors; false when it is not theirs (one cursor, read-only, an empty text: the view pastes as usual).</summary>
    public bool TryPaste(string text)
    {
        if (!HasMultipleCursors || !_host.CanUseCursors || string.IsNullOrEmpty(text))
        {
            return false;
        }
        CursorSet set = BuildCursorSet();
        // A text with a line for each cursor (what Copy puts on the clipboard at several cursors) gives each cursor its own line.
        string[] parts = MultiCursor.SplitForCursors(text, set.Count);
        _host.HideCompletion();
        ApplyCursorPlan(parts != null ? MultiCursor.InsertPerCursor(CursorText, set, parts) : MultiCursor.Insert(CursorText, set, text));
        return true;
    }

    /// <summary>
    /// Alt+click at <paramref name="hit"/> adds a cursor (or takes one away); with <paramref name="shift"/>, a cursor on every line from the last
    /// click down to it. The view checks the button, Alt and the place, and stops its own selection by the mouse.
    /// </summary>
    public void AltClick(TextDocument.TextPosition hit, bool shift)
    {
        IMultiCursorText text = CursorText;
        TextPoint point = ToPoint(hit);
        CursorSet set;
        if (shift)
        {
            set = MultiCursor.SelectColumns(text, _columnAnchor ?? ToPoint(_host.CaretPosition), point);
        }
        else
        {
            set = MultiCursor.ToggleAt(text, BuildCursorSet(), point);
            _columnAnchor = point;
        }
        _host.HideCompletion();
        ShowCursorSet(set);
    }
}
