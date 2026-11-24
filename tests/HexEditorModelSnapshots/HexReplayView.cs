using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;

// What Controls/HexEditorControl did around the models of the hex editor (HexEditingModel, HexViewLayout, HexPointer, HexKeyMap, HexByteText),
// without a window: the properties and the commands, the keys (OnKeyDown, OnKeyPress), the mouse (OnMouseDown, OnMouseMove, OnMouseUp,
// OnMouseWheel), the scroll bars (their visibility, the values mirror the layout), the focus and its StateChanged, and the events. The code is that
// of the control, with the painting, the timers, the tool tip and the clipboard of the system left out (the clipboard is a string here), so that
// the script of the old stand (tests/HexEditorControlTests, removed with the control in stage 3E-5) runs on the models and writes the same lines.
// The sizes are those of a window chosen by the test; the char cell is fixed (8 x 15): the stand found every point from the layout, so the lines
// do not depend on it.
internal sealed class HexReplayView : IHexEditorView, IHexLayoutView, IHexInspectorSource
{
    public const int CharWidth = 8;

    public const int CharHeight = 15;

    public const int ScrollBarSize = 16;

    private HexDocument _document = new HexDocument();

    private readonly HexEditingModel _model;

    private readonly HexViewLayout _layout;

    private readonly HexPointer _pointer;

    private readonly HexReplayInspector _inspector;

    private bool _colorByValue;

    private bool _showInspector;

    private bool _inspectorShown;

    private bool _verticalBarVisible;

    private bool _horizontalBarVisible;

    private bool _updatingLayout;

    // The window the view is shown in (the shown runs of the stand: a form of the size given); without one Focus does nothing.
    private bool _shown;

    private int _clientWidth;

    private int _clientHeight;

    public HexReplayView()
    {
        _model = new HexEditingModel(_document);
        _layout = new HexViewLayout(_model, this);
        _pointer = new HexPointer(_model);
        _model.Changed += OnModelChanged;
        _inspector = new HexReplayInspector(this);
        Document = _document;
        UpdateMetrics();
        UpdateInspectorVisibility();
        UpdateScrollBars();
    }

    public event EventHandler StateChanged;

    public event EventHandler FindRequested;

    public event EventHandler GoToOffsetRequested;

    public event EventHandler SaveRequested;

    public event EventHandler<HexReplaySearch> SearchFinished;

    public event EventHandler ViewOptionsChanged;

    /// <summary>What a copy put on the clipboard (the clipboard of the system is not used).</summary>
    public string Clipboard { get; set; } = "";

    public HexEditingModel Model => _model;

    public HexViewLayout Layout => _layout;

    public HexReplayInspector Inspector => _inspector;

    /// <summary>The control or one of its children has the focus (the shown runs).</summary>
    public HexReplayFocus FocusOwner { get; private set; }

    public RgbColor BackColor { get; private set; } = RgbColor.FromArgb(255, 255, 255);

    public RgbColor ForeColor { get; private set; } = RgbColor.FromArgb(0, 0, 0);

    public void SetBackColor(RgbColor color)
    {
        BackColor = color;
    }

    public void SetForeColor(RgbColor color)
    {
        ForeColor = color;
    }

    /// <summary>The view is shown in a window of this client size and takes the focus, as a control alone in a form does when the form is shown.</summary>
    public void Show(int width, int height)
    {
        _shown = true;
        SetClientSize(width, height);
        if (FocusOwner == HexReplayFocus.None)
        {
            SetFocus(HexReplayFocus.Editor);
        }
    }

    /// <summary>The window is closed (disposed): the view loses the focus, which it tells as a change of its state (OnLostFocus).</summary>
    public void Close()
    {
        SetFocus(HexReplayFocus.None);
        _shown = false;
    }

    /// <summary>The window was resized (OnResize).</summary>
    public void SetClientSize(int width, int height)
    {
        _clientWidth = width;
        _clientHeight = height;
        UpdateInspectorVisibility();
        UpdateScrollBars();
    }

    public HexDocument Document
    {
        get
        {
            return _document;
        }
        set
        {
            if (_document != null)
            {
                _document.Changed -= OnDocumentChanged;
                _document.ModifiedChanged -= OnDocumentModifiedChanged;
                _document.HighlightsChanged -= OnDocumentHighlightsChanged;
            }
            _document = value ?? new HexDocument();
            _model.Document = _document;
            _document.Changed += OnDocumentChanged;
            _document.ModifiedChanged += OnDocumentModifiedChanged;
            _document.HighlightsChanged += OnDocumentHighlightsChanged;
            _model.CaretOffset = Math.Min(_model.CaretOffset, _document.Length);
            _model.Anchor = -1;
            _model.Nibble = 0;
            UpdateScrollBars();
            OnStateChanged();
        }
    }

    public int Length => _document.Length;

    public int CaretOffset => _model.CaretOffset;

    public bool AsciiAreaActive
    {
        get
        {
            return _model.Area == HexEditArea.Ascii;
        }
        set
        {
            _model.Area = value ? HexEditArea.Ascii : HexEditArea.Hex;
        }
    }

    public bool ReadOnly
    {
        get
        {
            return _model.ReadOnly;
        }
        set
        {
            if (_model.ReadOnly == value)
            {
                return;
            }
            _model.ReadOnly = value;
            OnStateChanged();
        }
    }

    public bool OverwriteMode => _model.Overwrite;

    public bool InsertMode => !_model.Overwrite;

    public bool HasSelection => _model.Anchor >= 0 && _model.Anchor != _model.CaretOffset;

    public int SelectionStart => HasSelection ? Math.Min(_model.Anchor, _model.CaretOffset) : _model.CaretOffset;

    public int SelectionEnd => HasSelection ? Math.Max(_model.Anchor, _model.CaretOffset) : _model.CaretOffset;

    public int SelectionLength => HasSelection ? Math.Abs(_model.CaretOffset - _model.Anchor) : 0;

    public bool CanUndo => !_model.ReadOnly && _document.CanUndo;

    public bool CanRedo => !_model.ReadOnly && _document.CanRedo;

    public bool IsModified => _document.IsModified;

    public string LastSearchText => _model.LastSearchText;

    public bool Focused => FocusOwner == HexReplayFocus.Editor;

    public int BytesPerRow
    {
        get
        {
            return _model.BytesPerRow;
        }
        set
        {
            int bytesPerRow = Array.IndexOf(HexViewChoices.BytesPerRow, value) >= 0 ? value : 16;
            if (bytesPerRow == _model.BytesPerRow)
            {
                return;
            }
            _model.BytesPerRow = bytesPerRow;
            _layout.GroupSize = Math.Min(_layout.GroupSize, _model.BytesPerRow);
            OnLayoutOptionChanged();
            OnViewOptionsChanged();
        }
    }

    public int GroupSize
    {
        get
        {
            return _layout.GroupSize;
        }
        set
        {
            int groupSize = Array.IndexOf(HexViewChoices.GroupSize, value) >= 0 ? Math.Min(value, _model.BytesPerRow) : 1;
            if (groupSize == _layout.GroupSize)
            {
                return;
            }
            _layout.GroupSize = groupSize;
            OnLayoutOptionChanged();
            OnViewOptionsChanged();
        }
    }

    public bool BigEndian
    {
        get
        {
            return _model.BigEndian;
        }
        set
        {
            if (_model.BigEndian == value)
            {
                return;
            }
            _model.BigEndian = value;
            OnStateChanged();
            OnViewOptionsChanged();
        }
    }

    public bool ColorByValue
    {
        get
        {
            return _colorByValue;
        }
        set
        {
            if (_colorByValue == value)
            {
                return;
            }
            _colorByValue = value;
            OnStateChanged();
            OnViewOptionsChanged();
        }
    }

    public bool ShowDataInspector
    {
        get
        {
            return _showInspector;
        }
        set
        {
            if (_showInspector == value)
            {
                return;
            }
            _showInspector = value;
            UpdateInspectorVisibility();
            OnStateChanged();
            OnViewOptionsChanged();
        }
    }

    public void LoadViewOptions(int bytesPerRow, int groupSize, bool bigEndian, bool colorByValue, bool showDataInspector)
    {
        _model.BytesPerRow = Array.IndexOf(HexViewChoices.BytesPerRow, bytesPerRow) >= 0 ? bytesPerRow : 16;
        _layout.GroupSize = Array.IndexOf(HexViewChoices.GroupSize, groupSize) >= 0 ? Math.Min(groupSize, _model.BytesPerRow) : 1;
        _model.BigEndian = bigEndian;
        _colorByValue = colorByValue;
        _showInspector = showDataInspector;
        UpdateInspectorVisibility();
        OnLayoutOptionChanged();
        OnStateChanged();
    }

    public HexHighlight HighlightAtCaret => _document.GetHighlightAt(_model.CaretOffset);

    public bool HasHighlights => _document.Highlights.Count > 0;

    public bool CanHighlight => HasSelection || _model.CaretOffset < _document.Length;

    public int VisibleRows => _layout.VisibleRows;

    public int RowCount => _layout.RowCount;

    public bool CopySelection()
    {
        byte[] bytes = _model.GetSelectedBytes();
        if (bytes.Length == 0)
        {
            return false;
        }
        Clipboard = HexDocument.FormatHex(bytes);
        return true;
    }

    public void CopyAll()
    {
        Clipboard = HexDocument.FormatHex(_document.ToArray());
    }

    public void Cut()
    {
        if (_model.ReadOnly)
        {
            return;
        }
        if (!HasSelection && _model.CaretOffset >= _document.Length)
        {
            return;
        }
        if (CopySelection())
        {
            DeleteSelection();
        }
    }

    public void DeleteSelection()
    {
        if (_model.ReadOnly)
        {
            return;
        }
        _model.DeleteCurrentSelectionOrByte();
    }

    public void SelectAll()
    {
        _model.SelectAll();
    }

    public bool CopyAs(HexCopyFormat format)
    {
        byte[] bytes = _model.GetSelectedBytes();
        if (bytes.Length == 0)
        {
            return false;
        }
        string text = HexValueFormats.Format(bytes, format);
        if (text.Length == 0)
        {
            return false;
        }
        Clipboard = text;
        return true;
    }

    public byte[] GetChecksumBytes(out string description)
    {
        return _model.GetChecksumBytes(out description);
    }

    public bool FillSelection(byte[] pattern)
    {
        return _model.FillSelection(pattern);
    }

    public bool InsertPattern(int count, byte[] pattern)
    {
        return _model.InsertPattern(count, pattern);
    }

    public bool SwapSelectionByteOrder(int size, out string error)
    {
        return _model.SwapSelectionByteOrder(size, out error);
    }

    public bool WriteValue(HexValueType type, string text, out string error)
    {
        return _model.WriteValue(type, text, out error);
    }

    public byte[] GetBytesAtCaret(int count)
    {
        return _model.GetBytesAtCaret(count);
    }

    /// <summary>Marks the selected bytes with a colour (made opaque, as the control did with Color.FromArgb(255, color)) and a label.</summary>
    public bool HighlightSelection(RgbColor color, string label)
    {
        return _model.HighlightSelection(RgbColor.FromArgb(255, color.R, color.G, color.B).ToArgb(), label);
    }

    public bool RemoveHighlight()
    {
        return _model.RemoveHighlight();
    }

    public void ClearHighlights()
    {
        _model.ClearHighlights();
    }

    public bool GoToHighlight(bool forward)
    {
        // The control beeped when there was no block in that direction.
        return _model.GoToHighlight(forward);
    }

    public void Paste()
    {
        if (_model.ReadOnly || string.IsNullOrEmpty(Clipboard))
        {
            return;
        }
        // The stand never pastes (the clipboard of the system was not used); a text that can be both is taken as bytes here.
        byte[] bytes = HexPasteText.ToBytes(Clipboard, _model.Area, question => HexPasteChoice.Bytes);
        if (bytes != null)
        {
            _model.InsertBytes(bytes);
        }
    }

    public void Undo()
    {
        _model.Undo();
    }

    public void Redo()
    {
        _model.Redo();
    }

    public void ToggleOverwriteMode()
    {
        _model.ToggleOverwriteMode();
    }

    public void GoToOffset(int offset)
    {
        _model.GoToOffset(offset);
        Focus();
    }

    public bool Search(byte[] pattern, bool forward, bool ignoreCase, bool wrap)
    {
        int offset = _model.Search(pattern, forward, ignoreCase, wrap);
        if (offset == HexEditingModel.SearchSkipped)
        {
            return false;
        }
        if (offset < 0)
        {
            SearchFinished?.Invoke(this, new HexReplaySearch { Found = false });
            return false;
        }
        SearchFinished?.Invoke(this, new HexReplaySearch { Found = true, Offset = offset, Length = pattern.Length });
        return true;
    }

    public bool FindNext(bool forward)
    {
        if (_model.LastPattern == null)
        {
            FindRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }
        // The control beeped when nothing was found.
        return Search(_model.LastPattern, forward, _model.LastIgnoreCase, _model.LastWrap);
    }

    /// <summary>The key handling of OnKeyDown: true when the key was taken.</summary>
    public bool KeyDown(VirtualKey key, bool control, bool shift, bool alt)
    {
        HexKeyAction action = HexKeyMap.Map(key, control, shift, alt, out bool extendSelection);
        if (action == HexKeyAction.None)
        {
            return false;
        }
        if (!HexKeyMap.Apply(_model, action, extendSelection, VisibleRows))
        {
            switch (action)
            {
                case HexKeyAction.SelectAll:
                    SelectAll();
                    break;
                case HexKeyAction.Copy:
                    CopySelection();
                    break;
                case HexKeyAction.Cut:
                    Cut();
                    break;
                case HexKeyAction.Paste:
                    Paste();
                    break;
                case HexKeyAction.Undo:
                    Undo();
                    break;
                case HexKeyAction.Redo:
                    Redo();
                    break;
                case HexKeyAction.Find:
                    FindRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case HexKeyAction.GoToOffset:
                    GoToOffsetRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case HexKeyAction.ToggleDataInspector:
                    ShowDataInspector = !ShowDataInspector;
                    break;
                case HexKeyAction.Save:
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case HexKeyAction.ToggleOverwrite:
                    ToggleOverwriteMode();
                    break;
                case HexKeyAction.FindNext:
                    FindNext(true);
                    break;
                case HexKeyAction.FindPrevious:
                    FindNext(false);
                    break;
                case HexKeyAction.NextHighlight:
                    GoToHighlight(true);
                    break;
                case HexKeyAction.PreviousHighlight:
                    GoToHighlight(false);
                    break;
            }
        }
        return true;
    }

    /// <summary>A character typed (OnKeyPress).</summary>
    public bool KeyPress(char character)
    {
        return _model.TypeCharacter(character);
    }

    /// <summary>A press of the left (true) or the right button; another button does nothing (OnMouseDown; Shift is not held).</summary>
    public void MouseDown(HexReplayButton button, int x, int y)
    {
        Focus();
        if (button != HexReplayButton.Left && button != HexReplayButton.Right)
        {
            return;
        }
        if (y < _layout.HeaderHeight)
        {
            return;
        }
        int offset = _layout.OffsetFromPoint(x, y, out HexEditArea area, out int nibble);
        switch (_pointer.Press(button == HexReplayButton.Left ? HexPointerButton.Left : HexPointerButton.Right, offset, area, nibble, shift: false))
        {
            case HexPressResult.MovedCaret:
                EnsureVisible();
                OnStateChanged();
                return;
        }
    }

    public void MouseMove(HexReplayButton button, int x, int y)
    {
        if (!_pointer.Dragging || button != HexReplayButton.Left)
        {
            return;
        }
        _layout.ClampToTable(ref x, ref y, out bool outside);
        DragTo(x, y, ensureVisible: !outside);
    }

    private void DragTo(int x, int y, bool ensureVisible)
    {
        int offset = _layout.OffsetFromPoint(x, y, out HexEditArea area, out _);
        _pointer.DragTo(offset, area);
        if (ensureVisible)
        {
            EnsureVisible();
        }
        OnStateChanged();
    }

    public void MouseUp()
    {
        _pointer.Release();
    }

    /// <summary>The wheel without a modifier (OnMouseWheel).</summary>
    public void MouseWheel(int delta)
    {
        int rows = _layout.WheelRows(delta);
        if (rows == 0)
        {
            return;
        }
        SetFirstRow(_layout.FirstRow - rows);
    }

    public int ByteAtPoint(int x, int y)
    {
        return _layout.ByteAtPoint(x, y);
    }

    public string DescribeByte(int offset)
    {
        return HexByteText.Describe(_document, offset, _layout.GroupSize, _model.BigEndian);
    }

    public RgbColor ByteTextColor(byte value)
    {
        return HexByteText.TextColor(value, _colorByValue, BackColor, ForeColor);
    }

    // ---- the focus: the view, its inspector and the edit box of the inspector

    public void Focus()
    {
        SetFocus(HexReplayFocus.Editor);
    }

    /// <summary>Moves the focus (only while the view is shown): the one that had it hears that it lost it, then the new one that it got it.</summary>
    public void SetFocus(HexReplayFocus owner)
    {
        if (!_shown || owner == FocusOwner)
        {
            return;
        }
        HexReplayFocus previous = FocusOwner;
        FocusOwner = owner;
        switch (previous)
        {
            case HexReplayFocus.Editor:
                OnStateChanged();
                break;
            case HexReplayFocus.EditBox:
                _inspector.OnEditBoxLostFocus();
                break;
        }
        switch (owner)
        {
            case HexReplayFocus.Editor:
                OnStateChanged();
                break;
            case HexReplayFocus.Inspector:
                _inspector.OnGotFocus();
                break;
        }
    }

    // ---- the layout and the scroll bars

    private void UpdateInspectorVisibility()
    {
        if (_inspector == null)
        {
            return;
        }
        bool visible = _showInspector && _layout.InspectorFits(_inspector.PreferredWidth);
        if (_inspectorShown != visible)
        {
            _inspectorShown = visible;
            UpdateScrollBars();
        }
    }

    private void OnLayoutOptionChanged()
    {
        UpdateMetrics();
        UpdateInspectorVisibility();
        UpdateScrollBars();
        EnsureVisible();
    }

    private void SetFirstRow(int row)
    {
        _layout.FirstRow = _layout.ClampFirstRow(row);
    }

    private void EnsureVisible()
    {
        _layout.EnsureVisible();
        UpdateScrollBarValues();
    }

    private void UpdateScrollBars()
    {
        if (_updatingLayout)
        {
            return;
        }
        _updatingLayout = true;
        try
        {
            _layout.ChooseScrollBars(out bool needVertical, out bool needHorizontal);
            _verticalBarVisible = needVertical;
            _horizontalBarVisible = needHorizontal;
            UpdateScrollBarValues();
        }
        finally
        {
            _updatingLayout = false;
        }
    }

    // The values of the bars mirror the layout; what the control's UpdateScrollBarValues did to the layout is kept.
    private void UpdateScrollBarValues()
    {
        _layout.FirstRow = _layout.ClampFirstRow(_layout.FirstRow);
        _layout.ScrollX = HexScroll.ClampScrollX(_layout.ScrollX, _layout.ContentWidth, _layout.VisibleWidth);
    }

    private void UpdateMetrics()
    {
        _layout.SetMetrics(CharWidth, CharHeight);
    }

    private void OnModelChanged(HexChangeEffects effects)
    {
        if ((effects & HexChangeEffects.EnsureCaretVisible) != 0)
        {
            EnsureVisible();
        }
        OnStateChanged();
    }

    private void OnDocumentChanged(object sender, EventArgs e)
    {
        if (_model.CaretOffset > _document.Length)
        {
            _model.CaretOffset = _document.Length;
        }
        if (_model.Anchor > _document.Length)
        {
            _model.Anchor = _document.Length;
        }
        if (_model.Anchor == _model.CaretOffset)
        {
            _model.Anchor = -1;
        }
        UpdateScrollBars();
        OnStateChanged();
    }

    private void OnDocumentModifiedChanged(object sender, EventArgs e)
    {
        OnStateChanged();
    }

    private void OnDocumentHighlightsChanged(object sender, EventArgs e)
    {
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewOptionsChanged()
    {
        ViewOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // IRemoteHexView: the view of the remote editor (not used by the replay).
    bool IRemoteHexView.Focus()
    {
        Focus();
        return true;
    }

    void IRemoteHexView.Invalidate()
    {
    }

    int IHexLayoutView.ClientWidth => _clientWidth;

    int IHexLayoutView.ClientHeight => _clientHeight;

    int IHexLayoutView.InspectorWidth => _inspectorShown ? _inspector.PreferredWidth : 0;

    int IHexLayoutView.VerticalBarWidth => ScrollBarSize;

    int IHexLayoutView.HorizontalBarHeight => ScrollBarSize;

    bool IHexLayoutView.VerticalBarVisible => _verticalBarVisible;

    bool IHexLayoutView.HorizontalBarVisible => _horizontalBarVisible;
}

internal enum HexReplayButton
{
    None,
    Left,
    Right,
    Middle
}

internal enum HexReplayFocus
{
    None,
    Editor,
    Inspector,
    EditBox
}

/// <summary>What a search found (the control's HexSearchEventArgs).</summary>
internal sealed class HexReplaySearch : EventArgs
{
    public bool Found { get; set; }

    public int Offset { get; set; }

    public int Length { get; set; }
}
