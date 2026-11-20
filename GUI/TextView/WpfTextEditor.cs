using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// The text view on WPF (stage 3, step 3D-2): a view of our own on the models of the text view, not AvalonEdit. The caret moves are
/// <see cref="CaretNavigator"/>, the wrap map <see cref="WrapMap"/>, the selection <see cref="TextSelection"/>, the undo history
/// <see cref="UndoHistory"/>, the edits <see cref="TextReplace"/> with the commands of <c>TextEditCommands</c>, typing and the edit keys
/// <see cref="TextTyping"/>: the code the WinForms view (<c>Controls/TextEditorControl</c>) runs. The layout and the mouse selection are
/// <see cref="TextViewLayout"/> and <see cref="TextPointer"/>. It draws itself in whole pixels of the device: the layout counts in device pixels,
/// the drawing is scaled back by the DPI, and every row is one <see cref="GlyphRun"/> a colour on the grid of the characters of a monospaced font.
/// Not in it yet: the syntax colours, the folds, the inline messages, the completion, the marks of the blanks, the search highlights, characters
/// that the font has no glyph for (they are drawn as "?") and characters of two cells; it is tried in Tools > WPF text editor lab
/// (<see cref="TextLabWindow"/>) before it goes into the editor tab.
/// </summary>
internal sealed class WpfTextEditor : FrameworkElement, ITextLayoutView, ICaretNavigationHost, ITextReplaceHost, ITextTypingHost
{
    // What a word is for Ctrl+Left/Right, a double click and Ctrl+Backspace (as ReadonlyTextBrowser.IsWordChar).
    private const string WordCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private EditableTextDocument _document;

    private readonly TextViewLayout _layout;

    private readonly CaretNavigator _navigator;

    private readonly TextSelection _selection = new TextSelection();

    private readonly UndoHistory _history;

    private readonly TextReplace _replace;

    private readonly TextTyping _typing;

    private readonly TextPointer _pointer;

    // The tokens of the syntax of the lines (as CodeEditorPP keeps them) and the colours of the token types (none: the colour of the text).
    private readonly TokenCache _tokens = new TokenCache();

    private TokenCache.LineTokenizer _tokenizer;

    private RgbColor[] _tokenColors = new RgbColor[0];

    private readonly List<TextRun> _lineRuns = new List<TextRun>();

    private readonly VisualCollection _children;

    private readonly ScrollBar _verticalBar = new ScrollBar { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed };

    private readonly ScrollBar _horizontalBar = new ScrollBar { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };

    private readonly DispatcherTimer _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };

    // Scrolls while a selection is dragged beyond the edge of the text.
    private readonly DispatcherTimer _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new Dictionary<uint, SolidColorBrush>();

    private TextDocument.TextPosition _caret;

    private bool _caretVisible = true;

    private bool _readOnly;

    private bool _wordWrap;

    private bool _showLineNumbers = true;

    private bool _overwrite;

    private int _tabSize = 4;

    private bool _updatingScrollBars;

    private bool _updatingLayout;

    private RgbColor _backColor = RgbColor.FromArgb(255, 255, 255);

    private RgbColor _foreColor = RgbColor.FromArgb(0, 0, 0);

    private RgbColor _gutterBackColor = RgbColor.FromArgb(240, 240, 240);

    private RgbColor _gutterForeColor = RgbColor.FromArgb(128, 128, 128);

    private string _fontFamily = "Courier New";

    private double _fontPoints = 12;

    private GlyphTypeface _glyphTypeface;

    // The size of the font in device pixels.
    private double _emSize;

    private double _baseline;

    private double _scale = 1;

    /// <summary>The caret, the selection, the modified state or a mode changed.</summary>
    public event EventHandler StateChanged;

    /// <summary>The text became modified or saved.</summary>
    public event EventHandler ModifiedChanged;

    /// <summary>Ctrl+S.</summary>
    public event EventHandler SaveRequested;

    public WpfTextEditor()
    {
        _document = new EditableTextDocument(new[] { "" }, TextDocument.LineEnding.CrLf);
        _layout = new TextViewLayout(this);
        _navigator = new CaretNavigator(this);
        _history = new UndoHistory(OnModifiedChanged);
        _replace = new TextReplace(this, _history);
        _typing = new TextTyping(this);
        _pointer = new TextPointer(_selection, _navigator, () => _document);
        _tokens.SetDocument(_document, null);
        _children = new VisualCollection(this) { _verticalBar, _horizontalBar };
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        _verticalBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        _horizontalBar.ValueChanged += (sender, e) => OnScrollBarChanged();
        // The scroll bars keep their own pointer.
        _verticalBar.Cursor = Cursors.Arrow;
        _horizontalBar.Cursor = Cursors.Arrow;
        _caretTimer.Tick += (sender, e) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };
        _autoScrollTimer.Tick += (sender, e) => OnAutoScrollTick();
        ContextMenu = BuildContextMenu();
        UpdateMetrics();
        // The DPI of the monitor is known once the view is in a window (OnDpiChanged comes only when it changes later).
        Loaded += (sender, e) => OnLayoutOptionChanged();
    }

    // ---- the text and its state

    /// <summary>The text being edited. A new one starts with the caret at its start, no selection and an empty undo history.</summary>
    public EditableTextDocument Document
    {
        get
        {
            return _document;
        }
        set
        {
            EditableTextDocument document = value ?? new EditableTextDocument(new[] { "" }, TextDocument.LineEnding.CrLf);
            if (document.LineCount == 0)
            {
                // The view always has a line to put the caret in.
                document.SetText("");
            }
            _document = document;
            _tokens.SetDocument(_document, _tokenizer);
            _selection.Clear();
            _pointer.Release();
            _caret = new TextDocument.TextPosition(0, 0);
            _navigator.DesiredColumn = -1;
            _layout.OnDocumentChanged();
            _layout.FirstRow = 0;
            _layout.ScrollX = 0;
            // Its saved state is the empty undo history, unless the text was modified (as the WinForms view does).
            _history.Reset();
            UpdateScrollBars();
            InvalidateVisual();
            OnStateChanged();
        }
    }

    /// <summary>The caret (a column is an index into the line).</summary>
    public TextDocument.TextPosition CaretPosition => _caret;

    public bool HasSelection
    {
        get => _selection.HasSelection;
        set
        {
            _selection.HasSelection = value;
            InvalidateVisual();
        }
    }

    /// <summary>Where the selection starts; the caret is its other end.</summary>
    public TextDocument.TextPosition SelectionStart => _selection.Anchor;

    public TextDocument.TextPosition SelectionEnd => _caret;

    /// <summary>The selected text (lines end with CR LF), empty without a selection.</summary>
    public string SelectedText => HasSelection ? _document.GetRangeText(SelectionStart, _caret) : "";

    public bool IsModified
    {
        get => _history.IsModified;
        set => _history.IsModified = value;
    }

    public bool CanUndo => !_readOnly && _history.HasUndo;

    public bool CanRedo => !_readOnly && _history.HasRedo;

    // ---- options

    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            OnStateChanged();
        }
    }

    /// <summary>Typing replaces the character after the caret (Insert switches it).</summary>
    public bool OverwriteMode
    {
        get => _overwrite;
        set
        {
            _overwrite = value;
            InvalidateVisual();
            OnStateChanged();
        }
    }

    /// <summary>Tab and Indent insert TabSize spaces instead of a tab character.</summary>
    public bool InsertSpaces { get; set; }

    public int TabSize
    {
        get => _tabSize;
        set
        {
            _tabSize = Math.Max(1, value);
            _layout.UpdateMaxColumns(-1);
            _layout.Wrap.Invalidate();
            OnLayoutOptionChanged();
        }
    }

    /// <summary>Long lines are wrapped at the right edge instead of scrolling sideways.</summary>
    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (_wordWrap == value)
            {
                return;
            }
            // The first line in view stays in view: the rows count differently.
            int firstLine = _layout.RowCount > 0 ? _layout.RowToLine(_layout.FirstRow, out _, out _) : 0;
            _wordWrap = value;
            _layout.Wrap.Invalidate();
            _layout.ScrollX = 0;
            _layout.FirstRow = _layout.ClampFirstRow(_layout.PositionToRow(new TextDocument.TextPosition(0, firstLine)));
            OnLayoutOptionChanged();
        }
    }

    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set
        {
            _showLineNumbers = value;
            OnLayoutOptionChanged();
        }
    }

    /// <summary>The colours of the text and of the margin of the line numbers.</summary>
    public void SetColors(RgbColor background, RgbColor foreground, RgbColor marginBackground, RgbColor marginForeground)
    {
        _backColor = background;
        _foreColor = foreground;
        _gutterBackColor = marginBackground;
        _gutterForeColor = marginForeground;
        InvalidateVisual();
    }

    /// <summary>
    /// The syntax: the tokenizer of a language (null: none) and the colours of its token types, the first for the plain text (none: everything in
    /// the colour of the text); a token type past them takes the first, as in CodeEditorPP.
    /// </summary>
    public void SetSyntax(TokenCache.LineTokenizer tokenizer, RgbColor[] colors)
    {
        _tokenizer = tokenizer;
        _tokenColors = colors ?? new RgbColor[0];
        _tokens.SetDocument(_document, tokenizer);
        InvalidateVisual();
    }

    /// <summary>The font: a monospaced family and its size in points (before the scale of the monitor).</summary>
    public void SetFont(string familyName, double sizeInPoints)
    {
        _fontFamily = string.IsNullOrEmpty(familyName) ? "Courier New" : familyName;
        _fontPoints = sizeInPoints > 0 ? sizeInPoints : 12;
        _layout.Wrap.Invalidate();
        OnLayoutOptionChanged();
    }

    // ---- commands

    public void Undo()
    {
        if (!_readOnly && _history.Undo())
        {
            AfterEdit();
        }
    }

    public void Redo()
    {
        if (!_readOnly)
        {
            _history.Redo();
        }
        AfterEdit();
    }

    public void SelectAll()
    {
        _selection.Anchor = new TextDocument.TextPosition(0, 0);
        SetCaret(TextSelection.DocumentEnd(_document));
        _selection.HasSelection = true;
        InvalidateVisual();
        OnStateChanged();
    }

    /// <summary>Puts the selection, or the caret line with its line break when nothing is selected and <paramref name="select"/>, on the clipboard.</summary>
    public void CopySelection(bool select = true)
    {
        if (!HasSelection && !select)
        {
            return;
        }
        TryCopyToClipboard(GetTextToCopy());
    }

    public void Cut()
    {
        // The text is removed only when the clipboard has it.
        if (!TryCopyToClipboard(GetTextToCopy()))
        {
            return;
        }
        if (!HasSelection)
        {
            _typing.RemoveLines(_caret.Line, _caret.Line);
        }
        else
        {
            ReplaceText(_caret, SelectionStart, select: false, "");
        }
    }

    public void Paste()
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            ReplaceText(_caret, SelectionStart, !HasSelection, text);
            UpdateMaxLineWidth(-1);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>Removes the selected text (the clipboard is left alone).</summary>
    public void DeleteSelection()
    {
        _typing.DeleteSelection();
    }

    /// <summary>Replaces every occurrence of a text as one undo step and returns how many were replaced.</summary>
    public int ReplaceAll(string searchText, string replacement, bool ignoreCase)
    {
        if (_readOnly || string.IsNullOrEmpty(searchText))
        {
            return 0;
        }
        int count = _replace.ReplaceAll(TextMatcher.Literal(searchText, ignoreCase), replacement, TextDocument.TextPosition.Invalid, TextDocument.TextPosition.Invalid, out _);
        AfterEdit();
        return count;
    }

    /// <summary>Replaces the text between two positions (in either order); with <paramref name="select"/> the text goes in at <paramref name="start"/>.</summary>
    public void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
    {
        if (!_readOnly)
        {
            _replace.Replace(start, end, select, text);
        }
    }

    /// <summary>Indents (or, by a negative number, unindents) the selected lines or the caret line.</summary>
    public void Indent(int levels)
    {
        if (_readOnly)
        {
            return;
        }
        _history.Execute(new IndentCommand(this, levels));
    }

    /// <summary>Moves the caret (brought into the text) and scrolls it into view; the selection, if any, now ends there.</summary>
    public void SetCaret(TextDocument.TextPosition position)
    {
        int line = Math.Max(0, Math.Min(position.Line, _document.LineCount - 1));
        int column = Math.Max(0, Math.Min(position.Column, _document.GetLineText(line).Length));
        _caret = new TextDocument.TextPosition(column, line);
        _caretVisible = true;
        RestartCaretBlink();
        if (_layout.EnsureVisible(_caret))
        {
            UpdateScrollBarValues();
        }
        InvalidateVisual();
        OnStateChanged();
    }

    private void AfterEdit()
    {
        UpdateScrollBars();
        InvalidateVisual();
        OnStateChanged();
    }

    private string GetTextToCopy()
    {
        return HasSelection ? _document.GetRangeText(SelectionStart, _caret) : _document.GetLineText(_caret.Line) + "\r\n";
    }

    private bool TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return false;
        }
    }

    private void ShowError(Exception exception)
    {
        MessageDialog.Show(exception.Message, "Error", MessageDialogIcon.Error, MessageDialogButtons.Ok, defaultResult: null, owner: Window.GetWindow(this));
    }

    private ContextMenu BuildContextMenu()
    {
        ContextMenu menu = new ContextMenu();
        MenuItem undo = Item("_Undo", "Ctrl+Z", Undo);
        MenuItem redo = Item("_Redo", "Ctrl+Y", Redo);
        MenuItem cut = Item("Cu_t", "Ctrl+X", Cut);
        MenuItem copy = Item("_Copy", "Ctrl+C", () => CopySelection());
        MenuItem paste = Item("_Paste", "Ctrl+V", Paste);
        MenuItem delete = Item("_Delete", "Del", DeleteSelection);
        MenuItem selectAll = Item("Select _All", "Ctrl+A", SelectAll);
        menu.Items.Add(undo);
        menu.Items.Add(redo);
        menu.Items.Add(new Separator());
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Opened += (sender, e) =>
        {
            undo.IsEnabled = CanUndo;
            redo.IsEnabled = CanRedo;
            cut.IsEnabled = !_readOnly;
            paste.IsEnabled = !_readOnly;
            delete.IsEnabled = !_readOnly && HasSelection;
        };
        return menu;
    }

    private MenuItem Item(string header, string gesture, Action action)
    {
        MenuItem item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (sender, e) =>
        {
            action();
            Focus();
        };
        return item;
    }

    // ---- keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed || key == Key.DeadCharProcessed)
        {
            return;
        }
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        VirtualKey virtualKey = (VirtualKey)KeyInterop.VirtualKeyFromKey(key);
        // The edit keys and the Ctrl and Shift chords first, then the view's keys, as in TextEditorControl and its base class.
        TypingKeyResult result = _typing.KeyDown(virtualKey, control, alt, shift);
        bool handled = (result & TypingKeyResult.Handled) != 0 || (virtualKey == VirtualKey.Delete && !shift);
        if (!handled && control)
        {
            switch (virtualKey)
            {
                case VirtualKey.Up:
                case VirtualKey.Down:
                    {
                        // Ctrl+Up and Ctrl+Down scroll by a row and leave the caret where it is.
                        int firstRow = _layout.FirstRow;
                        SetFirstRow(firstRow + (virtualKey == VirtualKey.Up ? -1 : 1));
                        e.Handled = _layout.FirstRow != firstRow;
                        return;
                    }
                case VirtualKey.A:
                    // Ctrl+Alt is AltGr on many keyboards: AltGr+A types a letter there (ą, å).
                    if (!alt)
                    {
                        SelectAll();
                        handled = true;
                    }
                    break;
                case VirtualKey.S:
                    if (!alt && SaveRequested != null)
                    {
                        SaveRequested(this, EventArgs.Empty);
                        handled = true;
                    }
                    break;
            }
        }
        if (!handled)
        {
            handled = Navigate(virtualKey, control, shift);
        }
        if (!handled && virtualKey == VirtualKey.Insert)
        {
            if (!shift && !control)
            {
                OverwriteMode = !OverwriteMode;
            }
            handled = true;
        }
        if (!handled && !control && !alt)
        {
            // The characters the WinForms view gets as KeyPress: WPF sends them as text only sometimes (and Tab moves the focus).
            char typed = virtualKey == VirtualKey.Back ? '\b' : (virtualKey == VirtualKey.Return ? '\r' : (virtualKey == VirtualKey.Tab ? '\t' : '\0'));
            if (typed != '\0')
            {
                _typing.TypeCharacter(typed, shift);
                handled = true;
            }
        }
        if (handled)
        {
            // A key the editor took must not also be typed as text (WPF then raises no TextInput for it).
            e.Handled = true;
            AfterEdit();
        }
    }

    // The navigation keys of ReadonlyTextBrowser.OnKeyDown: without Shift the selection goes, with Shift it starts at the caret or goes on.
    private bool Navigate(VirtualKey key, bool control, bool shift)
    {
        if (!shift)
        {
            if (!TryGetNavigationTarget(key, control, out TextDocument.TextPosition target))
            {
                return false;
            }
            _selection.HasSelection = false;
            SetCaret(target);
            return true;
        }
        bool started = _selection.StartIfNone(_caret);
        if (TryGetNavigationTarget(key, control, out TextDocument.TextPosition extended))
        {
            SetCaret(extended);
            return true;
        }
        if (started)
        {
            _selection.HasSelection = false;
        }
        return false;
    }

    private bool TryGetNavigationTarget(VirtualKey key, bool control, out TextDocument.TextPosition target)
    {
        _navigator.BeginMove(CaretNavigator.KeepsColumn(key), _caret);
        if (!CaretNavigator.TryMapKey(key, control, out CaretMove move, out _))
        {
            target = _caret;
            return false;
        }
        target = _navigator.Move(move, control, _caret);
        return true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        bool typed = false;
        foreach (char character in e.Text)
        {
            // Enter, Tab and Backspace were taken by the key; the other control characters type nothing.
            if (char.IsControl(character))
            {
                continue;
            }
            _typing.TypeCharacter(character, false);
            typed = true;
        }
        if (typed)
        {
            e.Handled = true;
            AfterEdit();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _caretVisible = true;
        _caretTimer.Start();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _caretTimer.Stop();
        InvalidateVisual();
    }

    private void RestartCaretBlink()
    {
        _caretTimer.Stop();
        if (IsKeyboardFocused)
        {
            _caretTimer.Start();
        }
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
        if (e.Handled)
        {
            return;
        }
        Focus();
        DevicePoint(e.GetPosition(this), out int x, out int y);
        if (x >= _layout.VisibleWidth || y >= _layout.VisibleHeight)
        {
            // The corner between the scroll bars.
            return;
        }
        TextDocument.TextPosition position = _layout.HitTest(x, y);
        if (e.ChangedButton == MouseButton.Left)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool margin = x < _layout.GutterWidth;
            SetCaret(_pointer.Press(position, e.ClickCount, shift, _caret, lineUnit: margin));
            CaptureMouse();
            _autoScrollTimer.Start();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right && !IsInSelection(position))
        {
            // The context menu works on what is under the mouse.
            _selection.Clear();
            SetCaret(position);
        }
    }

    private bool IsInSelection(TextDocument.TextPosition position)
    {
        if (!HasSelection)
        {
            return false;
        }
        TextDocument.TextPosition start = SelectionStart < _caret ? SelectionStart : _caret;
        TextDocument.TextPosition end = SelectionStart < _caret ? _caret : SelectionStart;
        return !(position < start) && position < end;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        DevicePoint(e.GetPosition(this), out int x, out int y);
        if (!_pointer.Dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = x < _layout.GutterWidth ? Cursors.Arrow : Cursors.IBeam;
            return;
        }
        // Beyond the text the selection ends at its edge and the timer does the scrolling.
        DragTo(x, y);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _pointer.Release();
        _autoScrollTimer.Stop();
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void DragTo(int x, int y)
    {
        x = Math.Max(0, Math.Min(x, _layout.VisibleWidth - 1));
        y = Math.Max(0, Math.Min(y, _layout.VisibleRows * _layout.RowHeight - 1));
        TextDocument.TextPosition? caret = _pointer.DragTo(_layout.HitTest(x, y));
        if (caret.HasValue)
        {
            SetCaret(caret.Value);
        }
    }

    private void OnAutoScrollTick()
    {
        if (!_pointer.Dragging || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            // The button was let go where the view did not see it.
            EndDrag();
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
            _layout.ScrollX = _layout.ClampScrollX(_layout.ScrollX + pixels);
            UpdateScrollBarValues();
        }
        DragTo(x, y);
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
            _layout.ScrollX = _layout.ClampScrollX(_layout.ScrollX - rows * _layout.CharWidth * 2);
            UpdateScrollBarValues();
        }
        else
        {
            SetFirstRow(_layout.FirstRow - rows);
        }
        InvalidateVisual();
    }

    // ---- what the models read of the view

    TextDocument ITextLayoutView.Document => _document;

    TextDocument ICaretNavigationHost.Document => _document;

    int ITextLayoutView.ClientWidth => (int)Math.Round(ActualWidth * _scale);

    int ITextLayoutView.ClientHeight => (int)Math.Round(ActualHeight * _scale);

    int ITextLayoutView.VerticalBarWidth => (int)Math.Round(SystemParameters.VerticalScrollBarWidth * _scale);

    int ITextLayoutView.HorizontalBarHeight => (int)Math.Round(SystemParameters.HorizontalScrollBarHeight * _scale);

    bool ITextLayoutView.VerticalBarVisible => _verticalBar.Visibility == Visibility.Visible;

    bool ITextLayoutView.HorizontalBarVisible => _horizontalBar.Visibility == Visibility.Visible;

    int ICaretNavigationHost.VisibleRows => _layout.VisibleRows;

    bool ICaretNavigationHost.IsWrapping => _wordWrap;

    WrapMap ICaretNavigationHost.Wrap => _layout.Wrap;

    public bool IsWordChar(char ch)
    {
        return WordCharacters.IndexOf(ch) != -1;
    }

    // No folds and no rows between the lines yet: the lines before and after are the neighbours.
    int ICaretNavigationHost.GetPreviousRealLine(int line)
    {
        return Math.Max(0, line - 1);
    }

    int ICaretNavigationHost.GetNextRealLine(int line)
    {
        return Math.Min(_document.LineCount - 1, line + 1);
    }

    void ICaretNavigationHost.OnMoveBeyondDocument(ref TextDocument.TextPosition textPosition, int direction)
    {
    }

    void ITextEditView.GetSelectedLines(out int first, out int last)
    {
        _selection.GetSelectedLines(_caret, out first, out last);
    }

    void ITextEditView.SetSelection(bool select, TextDocument.TextPosition start)
    {
        _selection.HasSelection = select;
        _selection.Anchor = start;
        InvalidateVisual();
    }

    void ITextEditView.SetCaretColumn(int column)
    {
        SetCaret(new TextDocument.TextPosition(column, _caret.Line));
    }

    void ITextEditView.OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        _tokens.RetokenizeLine(line, _tokenizer);
        _layout.Wrap.Invalidate();
        if (line == _caret.Line && _caret.Column > _document.GetLineText(line).Length)
        {
            SetCaret(new TextDocument.TextPosition(_document.GetLineText(line).Length, line));
        }
        _layout.UpdateMaxColumns(line);
        OnTextChangedOutsideEdit();
    }

    void ITextEditView.OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        SyntaxColoring.ChangedLines(start, oldEnd, newEnd, out int firstLine, out int removedLines, out int addedLines);
        _tokens.OnLinesChanged(firstLine, removedLines, addedLines, _tokenizer);
        _layout.Wrap.Invalidate();
        _layout.UpdateMaxColumns(-1);
        OnTextChangedOutsideEdit();
    }

    // The edit commands decide the modified state when they finish; a change made any other way can never be undone back to the saved text.
    private void OnTextChangedOutsideEdit()
    {
        if (!_history.InEdit)
        {
            _history.IsModified = true;
        }
    }

    void ITextEditView.InvalidateLine(int line)
    {
        InvalidateVisual();
    }

    void ITextEditView.UpdateLayout()
    {
        UpdateScrollBars();
    }

    void ITextEditView.Redraw()
    {
        InvalidateVisual();
    }

    bool ITextReplaceHost.SingleLine => false;

    bool ITextReplaceHost.UseExistingLineEnding => true;

    string ITextReplaceHost.GetLineOrNull(int line)
    {
        return line >= _document.LineCount ? null : _document.GetLineText(line);
    }

    public string GetIndentation(int line)
    {
        string text = _document.GetLineText(line);
        int length = 0;
        while (length < text.Length && (text[length] == ' ' || text[length] == '\t'))
        {
            length++;
        }
        return text.Substring(0, length);
    }

    bool ITextTypingHost.IsEditable => true;

    bool ITextTypingHost.CopyLineWhenNoSelection => true;

    int ITextTypingHost.IndexToColumn(int line, int index)
    {
        return _document.IndexToColumn(line, index, _tabSize);
    }

    TextDocument.TextPosition ITextTypingHost.GetMovedPosition(CaretMove move)
    {
        return _navigator.Move(move, (Keyboard.Modifiers & ModifierKeys.Control) != 0, _caret);
    }

    void ITextTypingHost.UpdateMaxLineWidth(int line)
    {
        _layout.UpdateMaxColumns(line);
    }

    private void UpdateMaxLineWidth(int line)
    {
        _layout.UpdateMaxColumns(line);
    }

    void ITextTypingHost.OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition)
    {
    }

    void ITextTypingHost.OnCharTyped(char ch, TextDocument.TextPosition textPosition)
    {
    }

    // ---- layout and scrolling

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index)
    {
        return _children[index];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _verticalBar.Measure(availableSize);
        _horizontalBar.Measure(availableSize);
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
        _verticalBar.Arrange(new Rect(Math.Max(0, finalSize.Width - barWidth), 0, barWidth, Math.Max(0, finalSize.Height - (horizontal ? barHeight : 0))));
        _horizontalBar.Arrange(new Rect(0, Math.Max(0, finalSize.Height - barHeight), Math.Max(0, finalSize.Width - (vertical ? barWidth : 0)), barHeight));
        return finalSize;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateScrollBars();
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        OnLayoutOptionChanged();
    }

    /// <summary>The font, the tab size, the wrap or the margin changed: the rows and the columns move.</summary>
    private void OnLayoutOptionChanged()
    {
        UpdateMetrics();
        UpdateScrollBars();
        if (_layout.EnsureVisible(_caret))
        {
            UpdateScrollBarValues();
        }
        InvalidateVisual();
        OnStateChanged();
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
        _layout.FirstRow = _layout.ClampFirstRow((int)Math.Round(_verticalBar.Value));
        _layout.ScrollX = _layout.ClampScrollX((int)Math.Round(_horizontalBar.Value));
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
            _horizontalBar.Maximum = _wordWrap ? 0 : Math.Max(0, _layout.ContentWidth - width);
            _horizontalBar.ViewportSize = Math.Max(1, width);
            _horizontalBar.LargeChange = Math.Max(1, width);
            _horizontalBar.SmallChange = Math.Max(1, _layout.CharWidth);
            _layout.ScrollX = _layout.ClampScrollX(_layout.ScrollX);
            _horizontalBar.Value = _layout.ScrollX;
        }
        finally
        {
            _updatingScrollBars = false;
        }
        InvalidateVisual();
    }

    // ---- changes

    private void OnModifiedChanged()
    {
        ModifiedChanged?.Invoke(this, EventArgs.Empty);
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

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

    private static RgbColor FromWpf(Color color)
    {
        return RgbColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    // The coloured parts of a line: its tokens with the colour of their type.
    private List<TextRun> LineRuns(int line)
    {
        _lineRuns.Clear();
        if (line < _tokens.LineCount)
        {
            foreach (TokenCache.Token token in _tokens.GetLineTokens(line))
            {
                _lineRuns.Add(new TextRun(token.Start, token.End, SyntaxColoring.ColorIndex(token.Type, _tokenColors.Length)));
            }
        }
        return _lineRuns;
    }

    // Draws the characters from..to of a row text from x on the grid of the characters, the top of the row at y; only the ones in view.
    private void DrawText(DrawingContext context, string text, int from, int to, int x, int y, int left, int right, RgbColor color)
    {
        int charWidth = _layout.CharWidth;
        // The columns of the row that are in view.
        from = Math.Max(from, (left - x) / charWidth - 1);
        to = Math.Min(to, (right - x) / charWidth + 1);
        if (_glyphTypeface == null || from >= to)
        {
            return;
        }
        int count = to - from;
        ushort[] glyphs = new ushort[count];
        double[] advances = new double[count];
        IDictionary<int, ushort> map = _glyphTypeface.CharacterToGlyphMap;
        map.TryGetValue('?', out ushort missing);
        for (int i = 0; i < count; i++)
        {
            glyphs[i] = map.TryGetValue(text[from + i], out ushort glyph) ? glyph : missing;
            advances[i] = charWidth;
        }
        int textY = y + Math.Max(0, (_layout.RowHeight - _layout.TextHeight) / 2);
        GlyphRun run = new GlyphRun(_glyphTypeface, 0, false, _emSize, (float)_scale, glyphs, new Point(x + from * charWidth, textY + _baseline), advances, null, null, null, null, null, null);
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
        int height = _layout.VisibleHeight;
        int gutter = _layout.GutterWidth;
        int charWidth = _layout.CharWidth;
        int rowHeight = _layout.RowHeight;
        int rowCount = _layout.RowCount;
        int textLeft = _layout.TextLeft - (_wordWrap ? 0 : _layout.ScrollX);
        bool focused = IsKeyboardFocused;
        RgbColor highlight = FromWpf(SystemColors.HighlightColor);
        RgbColor selection = focused ? highlight : HexByteText.Blend(_backColor, highlight, 0.45f);
        RgbColor selectionText = focused ? FromWpf(SystemColors.HighlightTextColor) : _foreColor;
        bool selected = HasSelection && SelectionStart != _caret;

        context.PushClip(new RectangleGeometry(new Rect(gutter, 0, Math.Max(0, width - gutter), height)));
        try
        {
            for (int row = _layout.FirstRow; row < rowCount; row++)
            {
                int y = (row - _layout.FirstRow) * rowHeight;
                if (y >= height)
                {
                    break;
                }
                string text = _layout.RowText(row);
                int selectedFrom = 0;
                int selectedTo = 0;
                if (selected && _layout.SelectedColumns(row, SelectionStart, _caret, out selectedFrom, out selectedTo, out bool pastEnd))
                {
                    // The line break is drawn as one cell after the line.
                    int selectedWidth = (selectedTo - selectedFrom + (pastEnd ? 1 : 0)) * charWidth;
                    context.DrawRectangle(Brush(selection), null, new Rect(textLeft + selectedFrom * charWidth, y, selectedWidth, rowHeight));
                }
                // The colours of the syntax, and the selected part of each run in the colour of a selected text.
                foreach (TextRun run in _layout.RowRuns(row, LineRuns(_layout.RowToLine(row, out _, out _))))
                {
                    RgbColor color = (run.Color < 0 || run.Color >= _tokenColors.Length) ? _foreColor : _tokenColors[run.Color];
                    int from = Math.Min(run.To, Math.Max(run.From, selectedFrom));
                    int to = Math.Max(from, Math.Min(run.To, selectedTo));
                    DrawText(context, text, run.From, from, textLeft, y, gutter, width, color);
                    DrawText(context, text, from, to, textLeft, y, gutter, width, selectionText);
                    DrawText(context, text, to, run.To, textLeft, y, gutter, width, color);
                }
            }
            if (focused && _caretVisible)
            {
                _layout.PositionToPoint(_caret, out int caretX, out int caretY);
                if (caretY >= 0 && caretY < height)
                {
                    if (_overwrite)
                    {
                        // Overwrite: a block over the character that typing replaces.
                        context.DrawRectangle(Brush(RgbColor.FromArgb(110, _foreColor.R, _foreColor.G, _foreColor.B)), null, new Rect(caretX, caretY, charWidth, rowHeight));
                    }
                    else
                    {
                        int caretWidth = Math.Max(1, (int)Math.Round(1.5 * _scale));
                        context.DrawRectangle(Brush(_foreColor), null, new Rect(caretX, caretY, caretWidth, rowHeight));
                    }
                }
            }
        }
        finally
        {
            context.Pop();
        }

        if (gutter > 0)
        {
            context.DrawRectangle(Brush(_gutterBackColor), null, new Rect(0, 0, gutter, height));
            int caretLine = _caret.Line;
            for (int row = _layout.FirstRow; row < rowCount; row++)
            {
                int y = (row - _layout.FirstRow) * rowHeight;
                if (y >= height)
                {
                    break;
                }
                string number = _layout.LineNumberText(row);
                if (number == null)
                {
                    continue;
                }
                // Right-aligned, with a blank cell before the text; the caret line in the colour of the text.
                int x = gutter - (number.Length + 1) * charWidth;
                bool caretRow = _layout.RowToLine(row, out _, out _) == caretLine;
                DrawText(context, number, 0, number.Length, x, y, 0, gutter, caretRow ? _foreColor : _gutterForeColor);
            }
        }
    }
}
