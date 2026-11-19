using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Rendering;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI.TerminalView;

/// <summary>
/// The HEX view of a device stream on the WPF terminal (stage 2D): what <c>HexTerminalControl</c> did, without WinForms. Two panes, the hex
/// digits of the bytes and their characters, each a WPF terminal that is written row by row (<see cref="HexTerminalModel.FormatRows"/>); they
/// scroll together and mirror each other's selection. The stream, the chunks, the breaks, the layout and the match of the search are
/// <see cref="HexTerminalModel"/>; the search for a byte sequence goes through the stream, whatever rows the bytes are in.
/// </summary>
public sealed class WpfHexTerminal : Grid, IByteSearchTarget
{
    /// <summary>A pane of the HEX view: a terminal that sends nothing, whose typed characters go to the owner.</summary>
    private sealed class HexPane : WpfTerminal
    {
        public event Action<char> CharacterTyped;

        protected override bool OnTerminalText(TextCompositionEventArgs e)
        {
            foreach (char ch in e.Text ?? "")
            {
                CharacterTyped?.Invoke(ch);
            }
            return true;
        }

        protected override void OnTerminalKey(KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
            switch (key)
            {
                case Key.PageUp:
                case Key.PageDown:
                    Model.TopLine += key == Key.PageUp ? -Model.Rows : Model.Rows;
                    e.Handled = true;
                    return;
                case Key.Back:
                    // The text box of the panel takes it, as the WinForms control passed on its KeyPress.
                    CharacterTyped?.Invoke('\b');
                    e.Handled = true;
                    return;
                case Key.Return:
                    CharacterTyped?.Invoke('\r');
                    e.Handled = true;
                    return;
            }
            // The copy and paste keys of the view stay; nothing is sent from a HEX view.
            if (key == Key.Insert && modifiers != ModifierKeys.None)
            {
                return;
            }
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 && !(Keyboard.IsKeyDown(Key.RightAlt) && (modifiers & ModifierKeys.Control) != 0))
            {
                e.Handled = true;
            }
        }
    }

    private static readonly HighlightStyle MatchStyle = new HighlightStyle(RgbColor.FromArgb(95, 255, 190, 0), RgbColor.Empty, RgbColor.Empty);

    private readonly HexTerminalModel _model = new HexTerminalModel();

    private readonly HexPane _hex = new HexPane();

    private readonly HexPane _text = new HexPane();

    private readonly ColumnDefinition _textColumn;

    private readonly DispatcherTimer _resizeTimer;

    private int _resizeTicks;

    private DeviceTerminalSettings.HexTerminalSettings _settings;

    private bool _syncingSelection;

    private bool _syncingScroll;

    // The byte sequence whose matches are marked while the search bar is open (null: none), the marks, and what they were made for.
    private byte[] _highlightPattern;

    private readonly List<int> _hexHighlightIds = new List<int>();

    private readonly List<int> _textHighlightIds = new List<int>();

    private int _highlightTopLine = -1;

    private int _highlightRows;

    private int _highlightLength;

    private bool _highlightRefreshPending;

    public WpfHexTerminal()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        _textColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        ColumnDefinitions.Add(_textColumn);
        SetColumn(_hex, 0);
        SetColumn(_text, 1);
        _text.Margin = new Thickness(2, 0, 0, 0);
        Children.Add(_hex);
        Children.Add(_text);
        foreach (HexPane pane in new[] { _hex, _text })
        {
            pane.AutoWrap = false;
            pane.Model.ScrollbackLimit = int.MaxValue;
            pane.CharacterTyped += ch => CharacterTyped?.Invoke(ch);
            pane.ShortcutKey += (sender, e) => ShortcutKey?.Invoke(this, e);
            pane.ContextMenuRequested += (sender, where) => ContextMenuRequested?.Invoke(this, where);
        }
        _hex.Model.SelectionChanged += SyncSelectionToText;
        _text.Model.SelectionChanged += SyncSelectionToHex;
        _hex.Model.Changed += OnHexChanged;
        _text.Model.Changed += OnTextChanged;
        _hex.TerminalSizeChanged += (sender, columns, rows) => OnHexResized();
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _resizeTimer.Tick += OnResizeTimer;
        ApplyViewSettings();
    }

    /// <summary>A character typed in one of the panes (the panel puts it into its HEX input box).</summary>
    public event Action<char> CharacterTyped;

    /// <summary>A key the panes do not use, for the shortcuts of the panel.</summary>
    public event KeyEventHandler ShortcutKey;

    /// <summary>The right button: the context menu at a point of the screen.</summary>
    public event Action<WpfHexTerminal, System.Drawing.Point> ContextMenuRequested;

    /// <summary>The colour of the echo of what was typed; null until the settings are applied.</summary>
    public RgbColor? EchoColor;

    public DeviceTerminalSettings.HexTerminalSettings HexSettings
    {
        get => _settings ?? new DeviceTerminalSettings.HexTerminalSettings();
        set
        {
            _settings = value ?? new DeviceTerminalSettings.HexTerminalSettings();
            ApplyViewSettings();
        }
    }

    public bool HideCaret
    {
        get => _hex.HideCaret;
        set
        {
            _hex.HideCaret = value;
            _text.HideCaret = value;
        }
    }

    public bool CopyOnSelect
    {
        get => _hex.CopyOnSelect;
        set
        {
            _hex.CopyOnSelect = value;
            _text.CopyOnSelect = value;
        }
    }

    /// <summary>The panes, for the settings that every terminal of the window takes (font, palette).</summary>
    internal IEnumerable<WpfTerminal> Panes => new WpfTerminal[] { _hex, _text };

    /// <summary>The colours of the text and the background.</summary>
    public void SetColors(RgbColor? foreground, RgbColor? background)
    {
        _hex.SetDefaultColors(foreground, background);
        _text.SetDefaultColors(foreground, background);
    }

    /// <summary>The bytes received (no echo, no breaks).</summary>
    public byte[] AllBytes
    {
        get
        {
            List<byte> list = new List<byte>();
            foreach (HexChunk item in _model.Chunks)
            {
                if (!item.IsBreak && !item.IsEcho && item.Count > 0)
                {
                    list.AddRange(_model.Stream.GetRange(item.Start, item.Count));
                }
            }
            return list.ToArray();
        }
    }

    /// <summary>The text of the HEX pane: lowercase pairs of digits separated by blanks, a line per row.</summary>
    public string HexText
    {
        get
        {
            EditableTextDocument document = _hex.Model.Document;
            return document.LineCount == 0 ? "" : document.GetRangeText(new TextDocument.TextPosition(0, 0), document.EndPosition, line => "\r\n");
        }
    }

    /// <summary>
    /// The HEX pane for the search bar: text that is not a byte sequence is searched for in its rows; byte sequences are searched for in the
    /// stream (<see cref="FindNextBytes"/>).
    /// </summary>
    public ISearchTarget SearchTarget => _hex.SearchTarget;

    public void FocusTerminal()
    {
        _hex.FocusTerminal();
    }

    public new bool IsFocused => _hex.View.IsKeyboardFocused || _text.View.IsKeyboardFocused;

    private void ApplyViewSettings()
    {
        DeviceTerminalSettings.HexTerminalSettings settings = HexSettings;
        _text.Visibility = settings.ShowTextView ? Visibility.Visible : Visibility.Collapsed;
        _textColumn.Width = settings.ShowTextView ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _hex.ScrollBarVisible = !settings.ShowTextView;
        _text.ScrollBarVisible = true;
        UpdateBytesPerLine();
    }

    private void UpdateBytesPerLine()
    {
        int bytesPerLineLimit = Math.Max(1, _hex.GridSize.X / 3);
        // A maximum of 0 (or less) in the settings: the width of the window decides.
        int configuredMaximum = HexSettings.MaximumBytesPerLine;
        _model.BytesPerLine = configuredMaximum > 0 ? Math.Min(bytesPerLineLimit, configuredMaximum) : bytesPerLineLimit;
    }

    private void OnHexResized()
    {
        UpdateBytesPerLine();
        _resizeTicks = 0;
        _resizeTimer.Start();
    }

    // Data.

    public void AppendData(byte[] buffer, int count, bool isEcho = false)
    {
        if (buffer == null)
        {
            return;
        }
        if (_model.ShouldBreak(isEcho, HexSettings.BreaksAroundEcho, HexSettings.TimeoutForAutoBreak, DateTime.Now))
        {
            NewRow();
            _model.AddBreak();
        }
        count = _model.AddData(buffer, count, isEcho, DateTime.Now);
        RenderData(buffer, count, isEcho);
    }

    private void NewRow()
    {
        foreach (HexPane pane in new[] { _hex, _text })
        {
            pane.View.FlushOutput();
            pane.Model.MoveCursorBy(-pane.Model.CursorPosition.X, 1, scroll: true);
        }
    }

    private void RenderData(byte[] buffer, int count, bool isEcho)
    {
        TerminalDisplayModel hex = _hex.Model;
        TerminalDisplayModel text = _text.Model;
        _model.BeginLayout();
        try
        {
            int currentColumn = (hex.CursorPosition.X + 1) / 3;
            if (isEcho)
            {
                CellStyle echo = new CellStyle
                {
                    Override = new CellStyle.ForegroundOverride
                    {
                        Color = EchoColor
                    }
                };
                hex.CurrentStyle = echo;
                text.CurrentStyle = echo;
            }
            foreach (HexRow row in HexTerminalModel.FormatRows(buffer, count, currentColumn, _model.BytesPerLine))
            {
                // The bytes that go to the row the cursor is in, for the map from a byte to its row and column.
                _model.Stream.MapBytes(hex.CursorDocumentLine, row.ByteCount);
                hex.WriteText(row.Hex);
                text.WriteText(row.Text);
                if (row.EndsRow)
                {
                    NewRow();
                }
            }
        }
        finally
        {
            hex.CurrentStyle = CellStyle.Default;
            text.CurrentStyle = CellStyle.Default;
        }
    }

    public void Clear()
    {
        _model.Clear();
        RemoveMatchHighlights();
        // The selection of the old data goes too: the next search would start from it. The mirroring is off while both are reset.
        bool wasSyncing = _syncingSelection;
        _syncingSelection = true;
        try
        {
            _hex.Reset();
            _text.Reset();
            _hex.Model.ClearSelection();
            _text.Model.ClearSelection();
        }
        finally
        {
            _syncingSelection = wasSyncing;
        }
    }

    public void ArchiveScreen()
    {
        _hex.BeginNewSession();
        _text.BeginNewSession();
    }

    private void OnResizeTimer(object sender, EventArgs e)
    {
        if (_resizeTicks++ < 5)
        {
            return;
        }
        _resizeTimer.Stop();
        if (!_model.NeedsRelayout)
        {
            // The rows are laid out by the number of bytes in them: with the same number nothing changes.
            return;
        }
        // The match that is selected is the same bytes in the new rows: it is selected there again.
        int matchOffset = IsMatchSelected() ? _model.MatchOffset : -1;
        int matchLength = _model.MatchLength;
        _model.ClearMatch();
        RemoveMatchHighlights();
        _hex.Reset();
        _text.Reset();
        _model.BeginRelayout();
        foreach (HexChunk item in _model.Chunks)
        {
            if (item.IsBreak)
            {
                NewRow();
            }
            else
            {
                RenderData(_model.Stream.GetRange(item.Start, item.Count), item.Count, item.IsEcho);
            }
        }
        if (matchOffset >= 0)
        {
            SelectBytes(matchOffset, matchLength);
        }
        RefreshMatchHighlights();
    }

    // The selection and the scrolling of the two panes.

    private void SyncSelectionToText()
    {
        if (_syncingSelection)
        {
            return;
        }
        try
        {
            _syncingSelection = true;
            TerminalDisplayModel hex = _hex.Model;
            if (!hex.HasSelection)
            {
                _text.Model.ClearSelection();
                return;
            }
            TextDocument.TextPosition start = hex.SelectionAnchor;
            TextDocument.TextPosition end = hex.SelectionEnd;
            HexTerminalModel.HexSelectionToText(ref start, ref end);
            _text.Model.Select(start, end);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SyncSelectionToHex()
    {
        if (_syncingSelection)
        {
            return;
        }
        try
        {
            _syncingSelection = true;
            TerminalDisplayModel text = _text.Model;
            if (!text.HasSelection)
            {
                _hex.Model.ClearSelection();
                return;
            }
            TextDocument.TextPosition start = text.SelectionAnchor;
            TextDocument.TextPosition end = text.SelectionEnd;
            HexTerminalModel.TextSelectionToHex(ref start, ref end);
            _hex.Model.Select(start, end);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnHexChanged()
    {
        FollowScroll(_hex, _text);
        ScheduleHighlightRefresh();
    }

    private void OnTextChanged()
    {
        FollowScroll(_text, _hex);
    }

    private void FollowScroll(HexPane from, HexPane to)
    {
        if (_syncingScroll || from.Model.TopLine == to.Model.TopLine)
        {
            return;
        }
        try
        {
            _syncingScroll = true;
            to.Model.TopLine = from.Model.TopLine;
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    public bool HasSelection => _hex.HasSelection || _text.HasSelection;

    public void CopySelection()
    {
        // The selection is mirrored, so it is copied from the pane that has the focus (the HEX pane by default).
        HexPane source = _text.View.IsKeyboardFocused ? _text : _hex;
        if (!source.HasSelection)
        {
            source = source == _hex ? _text : _hex;
        }
        if (source.HasSelection)
        {
            source.CopySelection(select: false);
        }
    }

    // The search for a byte sequence (it does not stop at the end of a row) and the marks of its matches.

    /// <summary>
    /// Finds <paramref name="pattern"/> in the bytes on the screen (the echo too), whatever rows they are in; a match does not go across a
    /// break. Returns the offset of the match in the stream, or -1. Nothing is selected.
    /// </summary>
    public int FindBytes(byte[] pattern, int startOffset, bool backward, bool wrap)
    {
        return _model.Stream.Find(pattern, startOffset, backward, wrap);
    }

    public bool FindNextBytes(byte[] pattern, bool backward, bool wrapAround)
    {
        int found = FindBytes(pattern, GetSearchStart(backward), backward, wrapAround);
        if (found < 0 || !SelectBytes(found, pattern.Length))
        {
            _model.ClearMatch();
            _hex.Model.ClearSelection();
            return false;
        }
        return true;
    }

    public void SetByteHighlight(byte[] pattern)
    {
        _highlightPattern = (pattern != null && pattern.Length > 0) ? pattern : null;
        RefreshMatchHighlights();
    }

    private bool IsMatchSelected()
    {
        TerminalDisplayModel hex = _hex.Model;
        return _model.IsMatchSelected(hex.HasSelection, hex.SelectionAnchor, hex.SelectionEnd);
    }

    private int GetSearchStart(bool backward)
    {
        TerminalDisplayModel hex = _hex.Model;
        return _model.GetSearchStart(backward, IsMatchSelected(), hex.SelectionAnchor, hex.SelectionEnd);
    }

    // Selects the bytes in both panes, from the first digit of the first one to the last digit of the last one, and scrolls them into view.
    private bool SelectBytes(int offset, int length)
    {
        if (!_model.Stream.TryLocate(offset, out int firstLine, out int firstIndex) || !_model.Stream.TryLocate(offset + length - 1, out int lastLine, out int lastIndex))
        {
            return false;
        }
        TextDocument.TextPosition start = new TextDocument.TextPosition(HexTerminalModel.HexStartColumn(firstIndex), firstLine);
        TextDocument.TextPosition end = new TextDocument.TextPosition(HexTerminalModel.HexEndColumn(lastIndex), lastLine);
        TerminalDisplayModel hex = _hex.Model;
        // The same order as the search in the text of a terminal: the selection begins behind the match and ends at its start.
        hex.Select(end, start);
        if (start.Line < hex.TopLine || start.Line >= hex.TopLine + hex.Rows)
        {
            hex.TopLine = start.Line - hex.Rows / 2;
        }
        _model.SetMatch(offset, length, hex.SelectionAnchor, hex.SelectionEnd);
        return true;
    }

    private void RemoveMatchHighlights()
    {
        RemoveHighlights(_hex, _hexHighlightIds);
        RemoveHighlights(_text, _textHighlightIds);
        _highlightTopLine = -1;
    }

    private static void RemoveHighlights(HexPane pane, List<int> ids)
    {
        foreach (int id in ids)
        {
            try
            {
                pane.View.RemoveHighlight(id);
            }
            catch (Exception)
            {
                // The view dropped it already.
            }
        }
        ids.Clear();
    }

    // Marks the matches in the rows on the screen (and one row around them): the history has no limit.
    private void RefreshMatchHighlights()
    {
        RemoveMatchHighlights();
        if (_highlightPattern == null)
        {
            return;
        }
        TerminalDisplayModel hex = _hex.Model;
        _highlightTopLine = hex.TopLine;
        _highlightRows = hex.Rows;
        _highlightLength = _model.Stream.Length;
        int from = _model.Stream.OffsetAtOrAfter(_highlightTopLine - 1, 0);
        int to = _model.Stream.OffsetAtOrAfter(_highlightTopLine + _highlightRows + 1, 0);
        List<int> ranges = new List<int>();
        _model.Stream.CollectMatchRanges(_highlightPattern, from, to, ranges, 10000);
        for (int index = 0; index < ranges.Count; index += 2)
        {
            if (!_model.Stream.TryLocate(ranges[index], out int firstLine, out int firstIndex) || !_model.Stream.TryLocate(ranges[index + 1] - 1, out int lastLine, out int lastIndex))
            {
                continue;
            }
            _hexHighlightIds.Add(_hex.View.AddHighlight(new TextDocument.TextPosition(HexTerminalModel.HexStartColumn(firstIndex), firstLine), new TextDocument.TextPosition(HexTerminalModel.HexEndColumn(lastIndex), lastLine), MatchStyle));
            _textHighlightIds.Add(_text.View.AddHighlight(new TextDocument.TextPosition(firstIndex, firstLine), new TextDocument.TextPosition(lastIndex + 1, lastLine), MatchStyle));
        }
    }

    // The pane was scrolled, resized or got new bytes since the matches were marked: they are marked again a moment later.
    private void ScheduleHighlightRefresh()
    {
        if (_highlightPattern == null || _highlightRefreshPending)
        {
            return;
        }
        TerminalDisplayModel hex = _hex.Model;
        if (_highlightTopLine == hex.TopLine && _highlightRows == hex.Rows && _highlightLength == _model.Stream.Length)
        {
            return;
        }
        _highlightRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _highlightRefreshPending = false;
            if (!_hex.IsDisposed)
            {
                RefreshMatchHighlights();
            }
        }));
    }

    public void Dispose()
    {
        _resizeTimer.Stop();
        _hex.Dispose();
        _text.Dispose();
    }
}
