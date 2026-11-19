using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmarTTY.GUI.HexView;

/// <summary>
/// The hex editor on WPF (stage 3, step 3B-2): the view of <see cref="HexEditingModel"/> that <c>Controls/HexEditorControl</c> is for WinForms,
/// with the same layout (<see cref="HexViewLayout"/>), keys (<see cref="HexKeyMap"/>), mouse selection (<see cref="HexPointer"/>), tool tip and
/// colours (<see cref="HexByteText"/>) and the same public members, so that the editor tab can take it in its place (3B-3). It draws itself in
/// whole pixels of the device: the layout counts in device pixels, the drawing is scaled back by the DPI, and the text is put on the grid of the
/// characters with <see cref="GlyphRun"/>s, one for each run of bytes of one colour in a row. The data inspector beside the bytes is
/// <see cref="WpfHexInspector"/> (3B-3), shown, like in the WinForms view, when it is switched on and the window leaves room for the bytes; while it
/// is hidden the header has a link that opens it. The context menu is the tab's.
/// </summary>
internal sealed class WpfHexEditor : FrameworkElement, IHexEditorView, IHexLayoutView, IHexInspectorSource
{
    // How strongly a marked block tints the background (as in HexEditorControl).
    private const byte HighlightAlpha = 100;

    private const int Margin = HexViewLayout.Margin;

    private HexDocument _document = new HexDocument();

    private readonly HexEditingModel _model;

    private readonly HexViewLayout _layout;

    private readonly HexPointer _pointer;

    private readonly VisualCollection _children;

    private readonly ScrollBar _verticalBar = new ScrollBar { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed };

    private readonly ScrollBar _horizontalBar = new ScrollBar { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };

    private readonly WpfHexInspector _inspector;

    private readonly DispatcherTimer _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };

    // Scrolls the table while a selection is dragged beyond its edge, even when the mouse stands still there.
    private readonly DispatcherTimer _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new Dictionary<uint, SolidColorBrush>();

    private bool _caretVisible = true;

    private bool _colorByValue;

    private bool _showInspector;

    // Whether the inspector is laid out (it is switched on and the window leaves room for the bytes).
    private bool _inspectorShown;

    // Where the "Data inspector" link is drawn in the header while the inspector is hidden, in device pixels (empty: not drawn).
    private Int32Rect _inspectorLinkBounds = Int32Rect.Empty;

    private bool _updatingScrollBars;

    private bool _updatingLayout;

    // The byte the tool tip describes; -1 when the mouse is not over a byte.
    private int _toolTipOffset = -1;

    private RgbColor _backColor = RgbColor.FromArgb(255, 255, 255);

    private RgbColor _foreColor = RgbColor.FromArgb(0, 0, 0);

    private string _fontFamily = "Courier New";

    private double _fontPoints = 12;

    private GlyphTypeface _glyphTypeface;

    // The size of the font and the place of the baseline below the top of the text, in device pixels.
    private double _emSize;

    private double _baseline;

    private int _textHeight;

    private double _scale = 1;

    /// <summary>The caret, the selection, the bytes or the undo history changed (the tool bar and status bar refresh).</summary>
    public event EventHandler StateChanged;

    /// <summary>The user pressed Ctrl+F.</summary>
    public event EventHandler FindRequested;

    /// <summary>The user pressed Ctrl+G.</summary>
    public event EventHandler GoToOffsetRequested;

    /// <summary>The user pressed Ctrl+S.</summary>
    public event EventHandler SaveRequested;

    /// <summary>A search ran (also when F3 repeated it).</summary>
    public event EventHandler<HexSearchEventArgs> SearchFinished;

    /// <summary>The user changed how the bytes are shown: the settings keep it for the next file.</summary>
    public event EventHandler ViewOptionsChanged;

    public WpfHexEditor()
    {
        _model = new HexEditingModel(_document);
        _model.Changed += OnModelChanged;
        _layout = new HexViewLayout(_model, this);
        _pointer = new HexPointer(_model);
        _inspector = new WpfHexInspector(this) { Visibility = Visibility.Collapsed };
        _children = new VisualCollection(this) { _verticalBar, _horizontalBar, _inspector };
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        _verticalBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        _horizontalBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        _caretTimer.Tick += (sender, e) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };
        _autoScrollTimer.Tick += (sender, e) => OnAutoScrollTick();
        ToolTipService.SetInitialShowDelay(this, 600);
        ToolTipService.SetBetweenShowDelay(this, 200);
        ToolTipService.SetShowDuration(this, 15000);
        Document = _document;
        UpdateMetrics();
        // The DPI of the monitor is known once the view is in a window (OnDpiChanged comes only when it changes later).
        Loaded += (sender, e) => OnLayoutOptionChanged();
    }

    /// <summary>The bytes being edited.</summary>
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
            InvalidateVisual();
            OnStateChanged();
        }
    }

    public int Length => _document.Length;

    public int CaretOffset => _model.CaretOffset;

    /// <summary>The text cannot be changed (a read-only file, a preview of a huge file).</summary>
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
            InvalidateVisual();
            OnStateChanged();
        }
    }

    /// <summary>Typing replaces the byte under the caret instead of inserting a new one.</summary>
    public bool OverwriteMode => _model.Overwrite;

    public bool InsertMode => !_model.Overwrite;

    public bool HasSelection => _model.HasSelection;

    public int SelectionStart => _model.SelectionStart;

    public int SelectionEnd => _model.SelectionEnd;

    public int SelectionLength => HasSelection ? Math.Abs(_model.CaretOffset - _model.Anchor) : 0;

    public bool CanUndo => !_model.ReadOnly && _document.CanUndo;

    public bool CanRedo => !_model.ReadOnly && _document.CanRedo;

    public bool IsModified => _document.IsModified;

    public string LastSearchText => _model.LastSearchText;

    /// <summary>The number of bytes in a row: 8, 16 or 32.</summary>
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
            ViewOptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The bytes shown together without a space: 1 (single bytes), 2, 4 or 8 (16-, 32- and 64-bit words).</summary>
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
            ViewOptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Numbers are read and written most significant byte first; otherwise least significant first.</summary>
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
            _toolTipOffset = -1;
            InvalidateVisual();
            OnStateChanged();
            ViewOptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The bytes are coloured by their kind (zero, 0xFF, control characters, bytes above 0x7F).</summary>
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
            InvalidateVisual();
            OnStateChanged();
            ViewOptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The data inspector (the value at the caret as numbers, time and text) is shown beside the bytes.</summary>
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
            ViewOptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the view options saved in the settings, without reporting them as changed.</summary>
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

    /// <summary>The colour of the background and of the text (the editor colours of the theme).</summary>
    public void SetColors(RgbColor background, RgbColor foreground)
    {
        _backColor = background;
        _foreColor = foreground;
        _inspector.InvalidateVisual();
        InvalidateVisual();
    }

    /// <summary>The font of the bytes; a monospaced one (a font without glyphs of its own falls back to Courier New).</summary>
    public void SetFont(string familyName, double sizeInPoints)
    {
        _fontFamily = string.IsNullOrEmpty(familyName) ? "Courier New" : familyName;
        _fontPoints = sizeInPoints > 0 ? sizeInPoints : 12;
        UpdateMetrics();
        OnLayoutOptionChanged();
    }

    /// <summary>The coloured block under the caret, or null.</summary>
    public HexHighlight HighlightAtCaret => _document.GetHighlightAt(_model.CaretOffset);

    public bool HasHighlights => _document.Highlights.Count > 0;

    /// <summary>There are bytes to mark: the selection or the byte under the caret.</summary>
    public bool CanHighlight => HasSelection || _model.CaretOffset < _document.Length;

    // ---- commands (as in HexEditorControl, with the clipboard and the dialogs of WPF)

    /// <summary>Copies the selected bytes (the byte under the caret without a selection); false when nothing reached the clipboard.</summary>
    public bool CopySelection()
    {
        try
        {
            byte[] bytes = _model.GetSelectedBytes();
            if (bytes.Length == 0)
            {
                return false;
            }
            Clipboard.SetText(HexDocument.FormatHex(bytes));
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    public void CopyAll()
    {
        try
        {
            Clipboard.SetText(HexDocument.FormatHex(_document.ToArray()));
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
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
        // The bytes are removed only when they are on the clipboard.
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

    /// <summary>Copies the selected bytes (the byte under the caret without a selection) as <paramref name="format"/>.</summary>
    public bool CopyAs(HexCopyFormat format)
    {
        try
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
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    /// <summary>Inserts the bytes of the clipboard at the caret (asks when the text can be bytes or text).</summary>
    public void Paste()
    {
        if (_model.ReadOnly)
        {
            return;
        }
        try
        {
            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            byte[] bytes = HexPasteText.ToBytes(text, _model.Area, question =>
            {
                MessageBoxResult answer = MessageDialog.Show(question, "Paste", MessageDialogIcon.Question, MessageDialogButtons.YesNoCancel, defaultResult: null, owner: Window.GetWindow(this));
                return answer == MessageBoxResult.Yes ? HexPasteChoice.Bytes : (answer == MessageBoxResult.No ? HexPasteChoice.Text : HexPasteChoice.Cancel);
            });
            if (bytes == null)
            {
                return;
            }
            _model.InsertBytes(bytes);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>The bytes the checksums are computed for: the selection, or the whole file without one.</summary>
    public byte[] GetChecksumBytes(out string description)
    {
        return _model.GetChecksumBytes(out description);
    }

    /// <summary>Repeats <paramref name="pattern"/> over the selected bytes (one undo step); false without a selection.</summary>
    public bool FillSelection(byte[] pattern)
    {
        return _model.FillSelection(pattern);
    }

    /// <summary>Inserts <paramref name="count"/> bytes that repeat <paramref name="pattern"/> at the caret and selects them.</summary>
    public bool InsertPattern(int count, byte[] pattern)
    {
        return _model.InsertPattern(count, pattern);
    }

    /// <summary>Reverses the byte order of each <paramref name="size"/>-byte word in the selection (one undo step).</summary>
    public bool SwapSelectionByteOrder(int size, out string error)
    {
        return _model.SwapSelectionByteOrder(size, out error);
    }

    /// <summary>Writes <paramref name="text"/>, read as <paramref name="type"/> in the current byte order, over the bytes at the caret.</summary>
    public bool WriteValue(HexValueType type, string text, out string error)
    {
        return _model.WriteValue(type, text, out error);
    }

    /// <summary>Up to <paramref name="count"/> bytes from the caret (from the start of the selection).</summary>
    public byte[] GetBytesAtCaret(int count)
    {
        return _model.GetBytesAtCaret(count);
    }

    /// <summary>Marks the selected bytes (the byte under the caret without a selection) with a colour and a label.</summary>
    public bool HighlightSelection(RgbColor color, string label)
    {
        return _model.HighlightSelection(RgbColor.FromArgb(255, color.R, color.G, color.B).ToArgb(), label);
    }

    /// <summary>Removes the colour from the selected bytes, or the whole block under the caret without a selection.</summary>
    public bool RemoveHighlight()
    {
        return _model.RemoveHighlight();
    }

    public void ClearHighlights()
    {
        _model.ClearHighlights();
    }

    /// <summary>Selects the next coloured block after the caret (the previous one when <paramref name="forward"/> is false).</summary>
    public bool GoToHighlight(bool forward)
    {
        if (!_model.GoToHighlight(forward))
        {
            SystemSounds.Beep.Play();
            return false;
        }
        return true;
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

    /// <summary>Moves the caret to <paramref name="offset"/> (clamped to the document) and selects nothing.</summary>
    public void GoToOffset(int offset)
    {
        _model.GoToOffset(offset);
        Focus();
    }

    /// <summary>Finds <paramref name="pattern"/> and selects it, after the selection (before it backwards), from the other end with <paramref name="wrap"/>.</summary>
    public bool Search(byte[] pattern, bool forward, bool ignoreCase, bool wrap)
    {
        int offset = _model.Search(pattern, forward, ignoreCase, wrap);
        if (offset == HexEditingModel.SearchSkipped)
        {
            return false;
        }
        if (offset < 0)
        {
            SearchFinished?.Invoke(this, new HexSearchEventArgs { Found = false });
            return false;
        }
        SearchFinished?.Invoke(this, new HexSearchEventArgs { Found = true, Offset = offset, Length = pattern.Length });
        return true;
    }

    /// <summary>Repeats the last search (F3 and Shift+F3).</summary>
    public bool FindNext(bool forward)
    {
        if (_model.LastPattern == null)
        {
            FindRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }
        bool found = Search(_model.LastPattern, forward, _model.LastIgnoreCase, _model.LastWrap);
        if (!found)
        {
            SystemSounds.Beep.Play();
        }
        return found;
    }

    private void ShowError(Exception exception)
    {
        MessageDialog.Show(exception.Message, "Error", MessageDialogIcon.Error, MessageDialogButtons.Ok, defaultResult: null, owner: Window.GetWindow(this));
    }

    // ---- keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // The keys of the inspector and of its edit box are not for the bytes.
        if (e.Handled || !ReferenceEquals(e.OriginalSource, this))
        {
            return;
        }
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed || key == Key.DeadCharProcessed)
        {
            return;
        }
        ModifierKeys modifiers = Keyboard.Modifiers;
        VirtualKey virtualKey = (VirtualKey)KeyInterop.VirtualKeyFromKey(key);
        HexKeyAction action = HexKeyMap.Map(virtualKey, (modifiers & ModifierKeys.Control) != 0, (modifiers & ModifierKeys.Shift) != 0, (modifiers & ModifierKeys.Alt) != 0, out bool extendSelection);
        if (action == HexKeyAction.None)
        {
            return;
        }
        if (!HexKeyMap.Apply(_model, action, extendSelection, _layout.VisibleRows))
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
        // A key the editor took must not also be typed as text (WPF then raises no TextInput for it).
        e.Handled = true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || !ReferenceEquals(e.OriginalSource, this))
        {
            return;
        }
        bool typed = false;
        foreach (char character in e.Text)
        {
            typed |= _model.TypeCharacter(character);
        }
        e.Handled = typed;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _caretVisible = true;
        _caretTimer.Start();
        InvalidateVisual();
        OnStateChanged();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _caretTimer.Stop();
        InvalidateVisual();
        OnStateChanged();
    }

    // ---- mouse (points in device pixels, as the layout counts)

    private void DevicePoint(Point point, out int x, out int y)
    {
        x = (int)Math.Floor(point.X * _scale);
        y = (int)Math.Floor(point.Y * _scale);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        DevicePoint(e.GetPosition(this), out int x, out int y);
        if (e.ChangedButton == MouseButton.Left && Contains(_inspectorLinkBounds, x, y))
        {
            ShowDataInspector = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right)
        {
            return;
        }
        if (y < _layout.HeaderHeight)
        {
            // The column titles are not bytes.
            return;
        }
        int offset = _layout.OffsetFromPoint(x, y, out HexEditArea area, out int nibble);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (_pointer.Press(e.ChangedButton == MouseButton.Left ? HexPointerButton.Left : HexPointerButton.Right, offset, area, nibble, shift))
        {
            case HexPressResult.KeptSelection:
                InvalidateVisual();
                return;
            case HexPressResult.MovedCaret:
                _caretVisible = true;
                EnsureVisible();
                InvalidateVisual();
                OnStateChanged();
                return;
            case HexPressResult.StartedDrag:
                CaptureMouse();
                _autoScrollTimer.Start();
                e.Handled = true;
                return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        DevicePoint(e.GetPosition(this), out int x, out int y);
        if (!_pointer.Dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = Contains(_inspectorLinkBounds, x, y) ? Cursors.Hand : null;
            UpdateToolTip(x, y);
            return;
        }
        // Beyond the table the selection ends at its edge and the timer does the scrolling.
        _layout.ClampToTable(ref x, ref y, out bool outside);
        DragTo(x, y, ensureVisible: !outside);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        _pointer.Release();
        _autoScrollTimer.Stop();
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _toolTipOffset = -1;
        ToolTip = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift)
        {
            // Ctrl+wheel and the like are the window's (the zoom).
            base.OnMouseWheel(e);
            return;
        }
        e.Handled = true;
        int rows = _layout.WheelRows(e.Delta);
        if (rows == 0)
        {
            return;
        }
        if (modifiers == ModifierKeys.Shift)
        {
            _layout.ScrollX -= rows * _layout.CharWidth * 3;
            UpdateScrollBarValues();
        }
        else
        {
            SetFirstRow(_layout.FirstRow - rows);
        }
        InvalidateVisual();
    }

    private void DragTo(int x, int y, bool ensureVisible)
    {
        int offset = _layout.OffsetFromPoint(x, y, out HexEditArea area, out _);
        _pointer.DragTo(offset, area);
        _caretVisible = true;
        if (ensureVisible)
        {
            EnsureVisible();
        }
        InvalidateVisual();
        OnStateChanged();
    }

    private void OnAutoScrollTick()
    {
        if (!_pointer.Dragging || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            // The button was let go where the view did not see it.
            _pointer.Release();
            _autoScrollTimer.Stop();
            return;
        }
        DevicePoint(Mouse.GetPosition(this), out int x, out int y);
        _layout.AutoScrollStep(x, y, out int rows, out int pixels);
        if (rows == 0 && pixels == 0)
        {
            return;
        }
        if (rows != 0)
        {
            SetFirstRow(_layout.FirstRow + rows);
        }
        if (pixels != 0)
        {
            _layout.ScrollX += pixels;
            UpdateScrollBarValues();
        }
        _layout.ClampToTable(ref x, ref y, out _);
        DragTo(x, y, ensureVisible: false);
    }

    /// <summary>Describes the byte under the mouse: its offset, the word of its group, the coloured block it is in.</summary>
    private void UpdateToolTip(int x, int y)
    {
        int offset = _layout.ByteAtPoint(x, y);
        if (offset == _toolTipOffset)
        {
            return;
        }
        _toolTipOffset = offset;
        ToolTip = (offset >= 0) ? HexByteText.Describe(_document, offset, _layout.GroupSize, _model.BigEndian) : null;
    }

    // ---- layout and scrolling

    int IHexLayoutView.ClientWidth => (int)Math.Round(ActualWidth * _scale);

    int IHexLayoutView.ClientHeight => (int)Math.Round(ActualHeight * _scale);

    int IHexLayoutView.InspectorWidth => _inspectorShown ? _inspector.PreferredWidth : 0;

    int IHexLayoutView.VerticalBarWidth => (int)Math.Round(SystemParameters.VerticalScrollBarWidth * _scale);

    int IHexLayoutView.HorizontalBarHeight => (int)Math.Round(SystemParameters.HorizontalScrollBarHeight * _scale);

    bool IHexLayoutView.VerticalBarVisible => _verticalBar.Visibility == Visibility.Visible;

    bool IHexLayoutView.HorizontalBarVisible => _horizontalBar.Visibility == Visibility.Visible;

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index)
    {
        return _children[index];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _verticalBar.Measure(availableSize);
        _horizontalBar.Measure(availableSize);
        _inspector.Measure(availableSize);
        double width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double barWidth = SystemParameters.VerticalScrollBarWidth;
        double barHeight = SystemParameters.HorizontalScrollBarHeight;
        bool vertical = _verticalBar.Visibility == Visibility.Visible;
        bool horizontal = _horizontalBar.Visibility == Visibility.Visible;
        // The inspector takes the whole height at the right, the scroll bars the rest (as the docking of the WinForms view).
        double inspectorWidth = _inspectorShown ? Math.Min(finalSize.Width, _inspector.PreferredWidth / _scale) : 0;
        double right = Math.Max(0, finalSize.Width - inspectorWidth);
        _inspector.Arrange(new Rect(right, 0, inspectorWidth, finalSize.Height));
        _verticalBar.Arrange(new Rect(Math.Max(0, right - barWidth), 0, barWidth, Math.Max(0, finalSize.Height - (horizontal ? barHeight : 0))));
        _horizontalBar.Arrange(new Rect(0, Math.Max(0, finalSize.Height - barHeight), Math.Max(0, right - (vertical ? barWidth : 0)), barHeight));
        return finalSize;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateInspectorVisibility();
        UpdateScrollBars();
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMetrics();
        OnLayoutOptionChanged();
    }

    /// <summary>The row width, the groups or the font changed: the columns move.</summary>
    private void OnLayoutOptionChanged()
    {
        _toolTipOffset = -1;
        UpdateMetrics();
        UpdateInspectorVisibility();
        UpdateScrollBars();
        EnsureVisible();
        _inspector.InvalidateVisual();
        InvalidateVisual();
    }

    /// <summary>Shows the data inspector when it is switched on and leaves room for the bytes.</summary>
    private void UpdateInspectorVisibility()
    {
        // In a narrow window the bytes come first: the inspector returns when the window grows.
        bool visible = _showInspector && _layout.InspectorFits(_inspector.PreferredWidth);
        if (!visible)
        {
            // The header link changes with it.
            InvalidateVisual();
        }
        if (_inspectorShown != visible)
        {
            _inspectorShown = visible;
            _inspector.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            InvalidateArrange();
            UpdateScrollBars();
            InvalidateVisual();
        }
    }

    private void UpdateMetrics()
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0)
        {
            _scale = 1;
        }
        _glyphTypeface = FindGlyphTypeface(_fontFamily) ?? FindGlyphTypeface("Courier New") ?? FindGlyphTypeface("Consolas");
        // Points to device pixels: 96 DIPs an inch, 72 points an inch.
        _emSize = _fontPoints * 96.0 / 72.0 * _scale;
        int charWidth = 8;
        int textHeight = 16;
        if (_glyphTypeface != null && _glyphTypeface.CharacterToGlyphMap.TryGetValue('0', out ushort zero))
        {
            charWidth = (int)Math.Ceiling(_glyphTypeface.AdvanceWidths[zero] * _emSize);
            textHeight = (int)Math.Ceiling(_glyphTypeface.Height * _emSize);
            _baseline = Math.Round(_glyphTypeface.Baseline * _emSize);
        }
        _textHeight = textHeight;
        _layout.SetMetrics(charWidth, textHeight);
    }

    private static GlyphTypeface FindGlyphTypeface(string familyName)
    {
        Typeface typeface = new Typeface(new FontFamily(familyName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return typeface.TryGetGlyphTypeface(out GlyphTypeface glyphTypeface) ? glyphTypeface : null;
    }

    private void OnScrollBarChanged()
    {
        if (_updatingScrollBars)
        {
            return;
        }
        _layout.FirstRow = (int)Math.Round(_verticalBar.Value);
        _layout.ScrollX = (int)Math.Round(_horizontalBar.Value);
        InvalidateVisual();
    }

    private void SetFirstRow(int row)
    {
        _layout.FirstRow = _layout.ClampFirstRow(row);
        _updatingScrollBars = true;
        try
        {
            _verticalBar.Value = _layout.FirstRow;
        }
        finally
        {
            _updatingScrollBars = false;
        }
        InvalidateVisual();
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
            Visibility vertical = needVertical ? Visibility.Visible : Visibility.Collapsed;
            Visibility horizontal = needHorizontal ? Visibility.Visible : Visibility.Collapsed;
            if (_verticalBar.Visibility != vertical || _horizontalBar.Visibility != horizontal)
            {
                _verticalBar.Visibility = vertical;
                _horizontalBar.Visibility = horizontal;
                InvalidateArrange();
            }
            UpdateScrollBarValues();
        }
        finally
        {
            _updatingLayout = false;
        }
    }

    // A WPF scroll bar goes from 0 to the last first row (or offset), with the visible part as its viewport.
    private void UpdateScrollBarValues()
    {
        _updatingScrollBars = true;
        try
        {
            int rowCount = _layout.RowCount;
            int visibleRows = _layout.VisibleRows;
            _verticalBar.Minimum = 0;
            _verticalBar.Maximum = Math.Max(0, rowCount - visibleRows);
            _verticalBar.ViewportSize = Math.Max(1, visibleRows);
            _verticalBar.LargeChange = Math.Max(1, visibleRows);
            _verticalBar.SmallChange = 1;
            _layout.FirstRow = _layout.ClampFirstRow(_layout.FirstRow);
            _verticalBar.Value = _layout.FirstRow;

            int width = _layout.VisibleWidth;
            _horizontalBar.Minimum = 0;
            _horizontalBar.Maximum = Math.Max(0, _layout.ContentWidth - width);
            _horizontalBar.ViewportSize = Math.Max(1, width);
            _horizontalBar.LargeChange = Math.Max(1, width);
            _horizontalBar.SmallChange = Math.Max(1, _layout.CharWidth);
            _layout.ScrollX = HexScroll.ClampScrollX(_layout.ScrollX, _layout.ContentWidth, width);
            _horizontalBar.Value = _layout.ScrollX;
        }
        finally
        {
            _updatingScrollBars = false;
        }
        InvalidateVisual();
    }

    // ---- changes of the model and the document

    // What the model asked for after an operation, and then what every change needs.
    private void OnModelChanged(HexChangeEffects effects)
    {
        if ((effects & HexChangeEffects.ShowCaret) != 0)
        {
            _caretVisible = true;
        }
        if ((effects & HexChangeEffects.RestartCaretBlink) != 0)
        {
            _caretTimer.Stop();
            if (IsKeyboardFocused)
            {
                _caretTimer.Start();
            }
        }
        if ((effects & HexChangeEffects.EnsureCaretVisible) != 0)
        {
            EnsureVisible();
        }
        InvalidateVisual();
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
        InvalidateVisual();
        OnStateChanged();
    }

    private void OnDocumentModifiedChanged(object sender, EventArgs e)
    {
        InvalidateVisual();
        OnStateChanged();
    }

    private void OnDocumentHighlightsChanged(object sender, EventArgs e)
    {
        _toolTipOffset = -1;
        InvalidateVisual();
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        _inspector.InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- what the data inspector reads of the view

    /// <summary>The DPI scale of the monitor (device pixels in a DIP).</summary>
    internal double DeviceScale => _scale;

    /// <summary>The width of a character of the font, in device pixels.</summary>
    internal int CharWidthInPixels => _layout.CharWidth;

    /// <summary>The height of the text of the font, in device pixels.</summary>
    internal int TextHeightInPixels => _textHeight;

    /// <summary>The size of the font, in device pixels.</summary>
    internal double EmSizeInPixels => _emSize;

    /// <summary>The font of the bytes, for text that is not on the grid of the characters.</summary>
    internal Typeface TextTypeface => new Typeface(new FontFamily(_fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    internal RgbColor BackgroundColor => _backColor;

    internal RgbColor ForegroundColor => _foreColor;

    internal SolidColorBrush BrushOf(RgbColor color)
    {
        return Brush(color);
    }

    private static bool Contains(Int32Rect bounds, int x, int y)
    {
        return !bounds.IsEmpty && x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;
    }

    /// <summary>
    /// While the data inspector is hidden, the header shows a link that opens it, at its right end when it does not cover the column titles (as in
    /// the WinForms view).
    /// </summary>
    private void DrawInspectorLink(DrawingContext context, int width)
    {
        _inspectorLinkBounds = Int32Rect.Empty;
        if (_inspectorShown)
        {
            return;
        }
        string text = _showInspector ? "Data inspector: the window is too narrow" : "Data inspector (Ctrl+I)";
        int textWidth = text.Length * _layout.CharWidth;
        int x = width - textWidth - Margin;
        int titlesEnd = _layout.AsciiStart - _layout.ScrollX + 6 * _layout.CharWidth;
        if (x < titlesEnd)
        {
            return;
        }
        RgbColor link = HexByteText.IsDark(_backColor) ? RgbColor.FromArgb(120, 180, 255) : RgbColor.FromArgb(0, 90, 200);
        DrawText(context, text, x, 2, _showInspector ? HexByteText.Blend(_backColor, _foreColor, 0.55f) : link);
        if (!_showInspector)
        {
            _inspectorLinkBounds = new Int32Rect(x, 0, textWidth, _layout.HeaderHeight - 2);
        }
    }

    bool IRemoteHexView.Focus()
    {
        return Focus();
    }

    void IRemoteHexView.Invalidate()
    {
        InvalidateVisual();
    }

    bool IHexEditorView.Focused => IsKeyboardFocusWithin;

    // ---- drawing (in device pixels: the drawing is scaled back by the DPI)

    private SolidColorBrush Brush(RgbColor color)
    {
        if (!_brushes.TryGetValue(color.Argb, out SolidColorBrush brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();
            _brushes[color.Argb] = brush;
        }
        return brush;
    }

    // A marked block tints the background with its colour.
    private static RgbColor HighlightColor(HexHighlight highlight)
    {
        RgbColor color = RgbColor.FromArgb(highlight.Argb);
        return RgbColor.FromArgb(HighlightAlpha, color.R, color.G, color.B);
    }

    private static RgbColor FromWpf(Color color)
    {
        return RgbColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    // Draws characters from x on the grid of the characters, the top of the text at y.
    private void DrawText(DrawingContext context, string text, int x, int y, RgbColor color)
    {
        if (_glyphTypeface == null || string.IsNullOrEmpty(text))
        {
            return;
        }
        ushort[] glyphs = new ushort[text.Length];
        double[] advances = new double[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!_glyphTypeface.CharacterToGlyphMap.TryGetValue(text[i], out ushort glyph))
            {
                _glyphTypeface.CharacterToGlyphMap.TryGetValue('.', out glyph);
            }
            glyphs[i] = glyph;
            advances[i] = _layout.CharWidth;
        }
        GlyphRun run = new GlyphRun(_glyphTypeface, 0, false, _emSize, (float)_scale, glyphs, new Point(x, y + _baseline), advances, null, null, null, null, null, null);
        context.DrawGlyphRun(Brush(color), run);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Brush(_backColor), null, new Rect(0, 0, ActualWidth, ActualHeight));
        context.PushTransform(new ScaleTransform(1 / _scale, 1 / _scale));
        try
        {
            Render(context);
        }
        finally
        {
            context.Pop();
        }
    }

    private void Render(DrawingContext context)
    {
        int width = _layout.VisibleWidth;
        int clientHeight = (int)Math.Round(ActualHeight * _scale);
        int charWidth = _layout.CharWidth;
        int rowHeight = _layout.RowHeight;
        int scrollX = _layout.ScrollX;
        int bytesPerRow = _model.BytesPerRow;
        RgbColor dim = HexByteText.Blend(_backColor, _foreColor, 0.55f);
        RgbColor selection = FromWpf(SystemColors.HighlightColor);
        RgbColor selectionText = FromWpf(SystemColors.HighlightTextColor);
        RgbColor changed = RgbColor.FromArgb(50, 255, 150, 40);
        RgbColor changedText = HexByteText.Blend(_backColor, RgbColor.FromArgb(255, 165, 0), 0.25f);
        RgbColor caret = RgbColor.FromArgb(255, 165, 0);
        RgbColor separator = HexByteText.Blend(_backColor, _foreColor, 0.35f);
        bool focused = IsKeyboardFocused;

        // The offset column scrolls horizontally together with the hex and character columns.
        DrawText(context, "OFFSET", Margin - scrollX, 2, dim);
        for (int index = 0; index < bytesPerRow; index++)
        {
            int x = _layout.HexCellX(index) - scrollX;
            if (x + charWidth * 2 < 0 || x > width)
            {
                continue;
            }
            DrawText(context, index.ToString("X2", CultureInfo.InvariantCulture), x, 2, dim);
        }
        DrawText(context, "ASCII", _layout.AsciiStart - scrollX, 2, dim);
        DrawInspectorLink(context, width);
        context.DrawRectangle(Brush(separator), null, new Rect(0, _layout.HeaderHeight - 2, width, 1));

        int textYOffset = Math.Max(0, (rowHeight - _textHeight) / 2);
        int selectionStart = HasSelection ? SelectionStart : -1;
        int selectionEnd = HasSelection ? SelectionEnd : -1;
        int tableBottom = clientHeight - (_horizontalBar.Visibility == Visibility.Visible ? (int)Math.Round(SystemParameters.HorizontalScrollBarHeight * _scale) : 0);
        context.PushClip(new RectangleGeometry(new Rect(0, _layout.HeaderHeight, width, Math.Max(0, tableBottom - _layout.HeaderHeight))));
        try
        {
            StringBuilder run = new StringBuilder();
            for (int row = _layout.FirstRow; row < _layout.RowCount; row++)
            {
                int y = _layout.HeaderHeight + (row - _layout.FirstRow) * rowHeight;
                if (y >= clientHeight)
                {
                    break;
                }
                int offset = row * bytesPerRow;
                if (offset >= _document.Length)
                {
                    break;
                }
                DrawText(context, offset.ToString("X8", CultureInfo.InvariantCulture), Margin - scrollX, y + textYOffset, dim);
                int count = Math.Min(bytesPerRow, _document.Length - offset);
                RgbColor[] colors = new RgbColor[count];
                HexHighlight lastHighlight = null;
                for (int index = 0; index < count; index++)
                {
                    int position = offset + index;
                    int x = _layout.HexCellX(index) - scrollX;
                    bool selected = selectionStart >= 0 && position >= selectionStart && position < selectionEnd;
                    bool isChanged = _document.IsByteChanged(position);
                    HexHighlight highlight = (index == 0 || lastHighlight == null || !lastHighlight.Contains(position))
                        ? _document.GetHighlightAt(position)
                        : lastHighlight;
                    lastHighlight = highlight;
                    // A selection or a coloured block continues over the gap to the next byte of the same row.
                    bool joinsNext = index + 1 < bytesPerRow && position + 1 < _document.Length
                        && (selected ? position + 1 < selectionEnd : (highlight != null && highlight.Contains(position + 1)));
                    int fillWidth = joinsNext ? _layout.HexCellX(index + 1) - _layout.HexCellX(index) : charWidth * 2;
                    RgbColor? background = selected ? selection : (highlight != null ? HighlightColor(highlight) : (isChanged ? changed : (RgbColor?)null));
                    int asciiX = _layout.AsciiStart - scrollX + index * charWidth;
                    if (background.HasValue)
                    {
                        context.DrawRectangle(Brush(background.Value), null, new Rect(x, y, fillWidth, rowHeight));
                        context.DrawRectangle(Brush(background.Value), null, new Rect(asciiX, y, charWidth, rowHeight));
                    }
                    byte value = _document[position];
                    colors[index] = selected ? selectionText : (isChanged ? changedText : HexByteText.TextColor(value, _colorByValue, _backColor, _foreColor));
                    if (focused && _caretVisible && _model.CaretOffset == position && !selected)
                    {
                        int caretX = _model.Area == HexEditArea.Hex ? x + _model.Nibble * charWidth : asciiX;
                        context.DrawRectangle(Brush(caret), null, new Rect(caretX, y + rowHeight - 2, charWidth, 2));
                    }
                }
                // The digits and the characters of the bytes of one colour in a row are one run each (the gaps of the groups are blanks).
                int start = 0;
                while (start < count)
                {
                    int end = start + 1;
                    while (end < count && colors[end] == colors[start])
                    {
                        end++;
                    }
                    run.Clear();
                    for (int index = start; index < end; index++)
                    {
                        run.Append(_document[offset + index].ToString("X2", CultureInfo.InvariantCulture));
                        if (index + 1 < end && (index + 1) % _layout.GroupSize == 0)
                        {
                            run.Append(' ');
                        }
                    }
                    DrawText(context, run.ToString(), _layout.HexCellX(start) - scrollX, y + textYOffset, colors[start]);
                    run.Clear();
                    for (int index = start; index < end; index++)
                    {
                        byte value = _document[offset + index];
                        run.Append(HexDocument.IsPrintable(value) ? (char)value : '.');
                    }
                    DrawText(context, run.ToString(), _layout.AsciiStart - scrollX + start * charWidth, y + textYOffset, colors[start]);
                    start = end;
                }
            }
            if (_document.Length == 0)
            {
                DrawText(context, "The file is empty. Type hexadecimal digits to add bytes.", Margin - scrollX, _layout.HeaderHeight + textYOffset, dim);
            }
            if (focused && _caretVisible && _model.CaretOffset == _document.Length)
            {
                int row = _model.CaretOffset / bytesPerRow;
                int column = _model.CaretOffset % bytesPerRow;
                if (row >= _layout.FirstRow)
                {
                    int y = _layout.HeaderHeight + (row - _layout.FirstRow) * rowHeight;
                    int x = (_model.Area == HexEditArea.Ascii) ? _layout.AsciiStart - scrollX + column * charWidth : _layout.HexCellX(column) - scrollX;
                    context.DrawRectangle(Brush(caret), null, new Rect(x, y + rowHeight - 2, charWidth, 2));
                }
            }
        }
        finally
        {
            context.Pop();
        }
    }
}
