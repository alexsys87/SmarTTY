using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

public partial class HexTerminalControl : DpiAwareControl, IByteSearchTarget
{
    // The stream of the bytes with the map to their rows, the chunks it came in, the breaks, the layout and the match that the search selected
    // (TextEditing/HexTerminalModel.cs); the control writes the rows to its two views.
    private readonly HexTerminalModel _model = new HexTerminalModel();

    // The byte sequence whose matches are marked while the search bar is open (null: none), the marks, and what they
    // were made for: the first row on the screen, the number of rows and the number of bytes.
    private byte[] _highlightPattern;

    private readonly List<int> _hexHighlightIds = new List<int>();

    private readonly List<int> _textHighlightIds = new List<int>();

    private int _highlightTopLine = -1;

    private int _highlightRows;

    private int _highlightLength;

    private bool _highlightRefreshPending;

    private static readonly HighlightStyle MatchStyle = new HighlightStyle(Color.FromArgb(95, 255, 190, 0).ToRgb(), RgbColor.Empty, RgbColor.Empty);

    /// <summary>The colour of the echo of what was typed; null until the settings are applied.</summary>
    public RgbColor? EchoColor;

    private DeviceTerminalSettings.HexTerminalSettings _settings;

    private bool _syncingSelection;

    private int _resizeTicks;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Brush InactiveBrush
    {
        get
        {
            return termText.InactiveBrush;
        }
        set
        {
            termText.InactiveBrush = (termHex.InactiveBrush = value);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HideCaret
    {
        get
        {
            return termHex.HideCaret;
        }
        set
        {
            TerminalEmulatorBase terminalEmulatorBase = termHex;
            bool hideCaret = (termText.HideCaret = value);
            terminalEmulatorBase.HideCaret = hideCaret;
        }
    }

    /// <summary>Copies the selection to the clipboard as soon as it is made (applies to both the HEX and the text view).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CopyOnSelect
    {
        get
        {
            return termHex.CopyOnSelect;
        }
        set
        {
            termHex.CopyOnSelect = value;
            termText.CopyOnSelect = value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DeviceTerminalSettings.HexTerminalSettings HexSettings
    {
        get
        {
            return _settings ?? new DeviceTerminalSettings.HexTerminalSettings();
        }
        set
        {
            if (value == null)
            {
                value = new DeviceTerminalSettings.HexTerminalSettings();
            }
            _settings = value;
            termText.Visible = value.ShowTextView;
            termHex.VerticalScrollBarVisible = !value.ShowTextView;
            LayoutViews();
        }
    }

    public override Font Font
    {
        get
        {
            return termHex.Font;
        }
        set
        {
            TerminalEmulatorBase terminalEmulatorBase = termHex;
            Font font = (termText.Font = value);
            terminalEmulatorBase.Font = font;
        }
    }

    public override Color ForeColor
    {
        get
        {
            return termHex.ForeColor;
        }
        set
        {
            TerminalEmulatorBase terminalEmulatorBase = termHex;
            Color foreColor = (termText.ForeColor = value);
            terminalEmulatorBase.ForeColor = foreColor;
        }
    }

    public override Color BackColor
    {
        get
        {
            return termHex.BackColor;
        }
        set
        {
            TerminalEmulatorBase terminalEmulatorBase = termHex;
            Color backColor = (termText.BackColor = value);
            terminalEmulatorBase.BackColor = backColor;
        }
    }

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

    public string HexText => termHex.Text;

    /// <summary>
    /// The HEX dump editor. Its displayed text is lowercase HEX byte pairs separated by single spaces
    /// (e.g. "0a 0d"). The shared search bar searches it for text that is not a byte sequence; byte sequences are
    /// searched for in the stream of the bytes (<see cref="FindBytes"/>), not in the rows of this text.
    /// </summary>
    public TextEditorControl SearchTextEditor => termHex;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool TextScrollBarVisible
    {
        get
        {
            return termText.VerticalScrollBarVisible;
        }
        set
        {
            termText.VerticalScrollBarVisible = value;
        }
    }

    public HexTerminalControl()
    {
        InitializeComponent();
        termHex.Paint += OnHexPaint;
    }

    public void AppendData(byte[] buffer, int count, bool isEcho = false)
    {
        if (buffer == null)
        {
            return;
        }
        if (_model.ShouldBreak(isEcho, HexSettings.BreaksAroundEcho, HexSettings.TimeoutForAutoBreak, DateTime.Now))
        {
            termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, scroll: true);
            termText.MoveCursorBy(-termText.CursorPosition.X, 1, scroll: true);
            _model.AddBreak();
        }
        count = _model.AddData(buffer, count, isEcho, DateTime.Now);
        RenderData(buffer, count, isEcho);
    }

    private void RenderData(byte[] buffer, int count, bool isEcho)
    {
        _model.BeginLayout();
        try
        {
            int currentColumn = (termHex.CursorPosition.X + 1) / 3;
            if (isEcho)
            {
                termText.CurrentStyle = (termHex.CurrentStyle = new CellStyle
                {
                    Override = new CellStyle.ForegroundOverride
                    {
                        Color = EchoColor
                    }
                });
            }
            foreach (HexRow row in HexTerminalModel.FormatRows(buffer, count, currentColumn, _model.BytesPerLine))
            {
                // The bytes that go to the row the cursor is in, for the map from a byte to its row and column.
                _model.Stream.MapBytes(termHex.CursorDocumentLine, row.ByteCount);
                termHex.WriteText(row.Hex);
                termText.WriteText(row.Text);
                if (row.EndsRow)
                {
                    termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, scroll: true);
                    termText.MoveCursorBy(-termText.CursorPosition.X, 1, scroll: true);
                }
            }
        }
        finally
        {
            termText.CurrentStyle = (termHex.CurrentStyle = CellStyle.Default);
        }
    }

    public void Clear()
    {
        _model.Clear();
        RemoveMatchHighlights();
        termHex.Reset();
        termText.Reset();
        // The selection of the old data goes too: its positions would be where the next search starts, in rows that are not the same any more.
        // The two views mirror each other's selection, which is switched off while both are reset.
        bool wasSyncing = _syncingSelection;
        _syncingSelection = true;
        try
        {
            termHex.SetSelection(select: false, TextDocument.TextPosition.Invalid);
            termHex.SetSelectionEnd(TextDocument.TextPosition.Invalid);
            termText.SetSelection(select: false, TextDocument.TextPosition.Invalid);
            termText.SetSelectionEnd(TextDocument.TextPosition.Invalid);
        }
        finally
        {
            _syncingSelection = wasSyncing;
        }
    }

    public void ArchiveScreen()
    {
        termHex.ArchiveScreen();
        termText.ArchiveScreen();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutViews();
        _resizeTimer.Enabled = true;
        _resizeTicks = 0;
    }

    private void LayoutViews()
    {
        if (HexSettings.ShowTextView)
        {
            int hexScrollBarWidth = termHex.ScrollBarWidth;
            int textScrollBarWidth = termText.ScrollBarWidth;
            int textWidth = Width - SystemInformation.VerticalScrollBarWidth - hexScrollBarWidth - textScrollBarWidth;
            termText.Width = textWidth / 4 + textScrollBarWidth + SystemInformation.VerticalScrollBarWidth;
            termHex.Width = Width - termText.Width - 2;
        }
        else
        {
            termHex.Width = Width;
        }
        int bytesPerLineLimit = termHex.TerminalSize.Width / 3;
        if (bytesPerLineLimit < 1)
        {
            bytesPerLineLimit = 1;
        }
        // A maximum of 0 (or less) was typed in the settings: the width of the window decides, instead of one byte on every line.
        int configuredMaximum = HexSettings.MaximumBytesPerLine;
        _model.BytesPerLine = configuredMaximum > 0 ? Math.Min(bytesPerLineLimit, configuredMaximum) : bytesPerLineLimit;
    }

    private void SyncSelectionToText(ReadonlyTextBrowser readonlyTextBrowser, TextDocument.TextPosition previousCaret, TextDocument.TextPosition currentCaret, bool select)
    {
        if (_syncingSelection)
        {
            return;
        }
        try
        {
            _syncingSelection = true;
            TextDocument.TextPosition selectionStart = termHex.SelectionStart;
            TextDocument.TextPosition selectionEnd = termHex.SelectionEnd;
            HexTerminalModel.HexSelectionToText(ref selectionStart, ref selectionEnd);
            termText.SetSelection(((ReadonlyTextBrowser)termHex).HasSelection, new TextDocument.TextPosition(selectionStart.Column, selectionStart.Line));
            termText.SetSelectionEnd(new TextDocument.TextPosition(selectionEnd.Column, selectionEnd.Line));
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SyncSelectionToHex(ReadonlyTextBrowser readonlyTextBrowser, TextDocument.TextPosition previousCaret, TextDocument.TextPosition currentCaret, bool select)
    {
        if (_syncingSelection)
        {
            return;
        }
        try
        {
            _syncingSelection = true;
            TextDocument.TextPosition selectionStart = termText.SelectionStart;
            TextDocument.TextPosition selectionEnd = termText.SelectionEnd;
            HexTerminalModel.TextSelectionToHex(ref selectionStart, ref selectionEnd);
            termHex.SetSelection(((ReadonlyTextBrowser)termText).HasSelection, new TextDocument.TextPosition(selectionStart.Column, selectionStart.Line));
            termHex.SetSelectionEnd(new TextDocument.TextPosition(selectionEnd.Column, selectionEnd.Line));
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    public bool HasSelection => ((ReadonlyTextBrowser)termHex).HasSelection || ((ReadonlyTextBrowser)termText).HasSelection;

    public void CopySelection()
    {
        // Selection is mirrored between both views, so copy from the focused one (hex view by default)
        TerminalEmulatorBase source = termText.Focused ? termText : termHex;
        if (!((ReadonlyTextBrowser)source).HasSelection)
        {
            source = source == termHex ? termText : termHex;
        }
        if (((ReadonlyTextBrowser)source).HasSelection)
        {
            source.CopySelection(select: false);
        }
    }

    private void termHex_KeyPress(object sender, KeyPressEventArgs e)
    {
        OnKeyPress(e);
    }

    private void termView_KeyDown(object sender, KeyEventArgs e)
    {
        // Let the owner handle clipboard shortcuts typed inside the inner views
        OnKeyDown(e);
    }

    private void termText_Scroll(object sender, ScrollEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }
        try
        {
            _syncingSelection = true;
            if (e.ScrollOrientation == ScrollOrientation.VerticalScroll)
            {
                termHex.TopLine = termText.TopLine;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void termHex_Scroll(object sender, ScrollEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }
        try
        {
            _syncingSelection = true;
            if (e.ScrollOrientation == ScrollOrientation.VerticalScroll)
            {
                termText.TopLine = termHex.TopLine;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnResizeTimer(object sender, EventArgs e)
    {
        if (_resizeTicks++ < 5)
        {
            return;
        }
        _resizeTimer.Enabled = false;
        if (!_model.NeedsRelayout)
        {
            // The lines are laid out by the number of bytes in them: with the same number nothing changes, and the whole
            // history (all of it is kept) was laid out again, and the scroll position lost, at every change of the size.
            return;
        }
        // The match that is selected is the same bytes in the new rows: it is selected again there.
        int matchOffset = IsMatchSelected() ? _model.MatchOffset : -1;
        int matchLength = _model.MatchLength;
        _model.ClearMatch();
        RemoveMatchHighlights();
        termHex.Reset();
        termText.Reset();
        _model.BeginRelayout();
        foreach (HexChunk item in _model.Chunks)
        {
            if (item.IsBreak)
            {
                termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, scroll: true);
                termText.MoveCursorBy(-termText.CursorPosition.X, 1, scroll: true);
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

    // ---- the search for a byte sequence (it does not stop at the end of a row) and the marks of its matches

    /// <summary>
    /// Finds <paramref name="pattern"/> in the bytes on the screen (the echo too), whatever rows they are in.
    /// Forward: the first match that begins at or after <paramref name="startOffset"/>; backward: the last one that
    /// begins at or before it. A match does not go across a break (an echo, a pause in the data: another message).
    /// Returns the offset of the match in the stream, or -1. Nothing is selected.
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
            // As the search in the text of a terminal does when it finds nothing.
            _model.ClearMatch();
            termHex.SetSelection(select: false, TextDocument.TextPosition.Invalid);
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
        return _model.IsMatchSelected(((ReadonlyTextBrowser)termHex).HasSelection, termHex.SelectionStart, termHex.SelectionEnd);
    }

    // Where the search begins: one byte behind (or before) the start of the match found last, so that overlapping
    // matches are found one by one; otherwise behind the selection or the caret (before it, backward).
    private int GetSearchStart(bool backward)
    {
        return _model.GetSearchStart(backward, IsMatchSelected(), termHex.SelectionStart, termHex.SelectionEnd);
    }

    // Selects the bytes in the HEX view, from the first digit of the first one to the last digit of the last one (in
    // as many rows as they take), and in the text view, and scrolls them into view.
    private bool SelectBytes(int offset, int length)
    {
        if (!_model.Stream.TryLocate(offset, out int firstLine, out int firstIndex) || !_model.Stream.TryLocate(offset + length - 1, out int lastLine, out int lastIndex))
        {
            return false;
        }
        TextDocument.TextPosition start = new TextDocument.TextPosition(HexTerminalModel.HexStartColumn(firstIndex), firstLine);
        TextDocument.TextPosition end = new TextDocument.TextPosition(HexTerminalModel.HexEndColumn(lastIndex), lastLine);
        // The same order as the search in the text of a terminal: the selection begins behind the match and ends at its start.
        termHex.SetSelectionEnd(start);
        termHex.SetSelection(select: true, end);
        // The text view follows when the start of the selection changes; a match that ends where the last one did changes only the end.
        SyncSelectionToText(termHex, start, start, select: true);
        termHex.Redraw();
        termText.Redraw();
        termHex.ScrollIntoView(start);
        termText.TopLine = termHex.TopLine;
        _model.SetMatch(offset, length, termHex.SelectionStart, termHex.SelectionEnd);
        return true;
    }

    private void RemoveMatchHighlights()
    {
        RemoveHighlights(termHex, _hexHighlightIds);
        RemoveHighlights(termText, _textHighlightIds);
        _highlightTopLine = -1;
    }

    private static void RemoveHighlights(TerminalEmulatorBase view, List<int> ids)
    {
        foreach (int id in ids)
        {
            try
            {
                view.RemoveHighlight(id);
            }
            catch (Exception)
            {
                // The view dropped it already.
            }
        }
        ids.Clear();
    }

    // Marks the matches in the rows on the screen (and one row around them). Only these rows are searched: the history
    // has no limit.
    private void RefreshMatchHighlights()
    {
        RemoveMatchHighlights();
        if (_highlightPattern == null)
        {
            return;
        }
        _highlightTopLine = termHex.TopLine;
        _highlightRows = termHex.VisibleRowCount;
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
            _hexHighlightIds.Add(termHex.AddHighlight(new TextDocument.TextPosition(HexTerminalModel.HexStartColumn(firstIndex), firstLine), new TextDocument.TextPosition(HexTerminalModel.HexEndColumn(lastIndex), lastLine), MatchStyle));
            _textHighlightIds.Add(termText.AddHighlight(new TextDocument.TextPosition(firstIndex, firstLine), new TextDocument.TextPosition(lastIndex + 1, lastLine), MatchStyle));
        }
    }

    // The view was scrolled, resized or got new bytes since the matches were marked: they are marked again, once the
    // painting is over (the marks belong to the rows on the screen).
    private void OnHexPaint(object sender, PaintEventArgs e)
    {
        if (_highlightPattern == null || _highlightRefreshPending || !IsHandleCreated)
        {
            return;
        }
        if (_highlightTopLine == termHex.TopLine && _highlightRows == termHex.VisibleRowCount && _highlightLength == _model.Stream.Length)
        {
            return;
        }
        _highlightRefreshPending = true;
        BeginInvoke(new Action(() =>
        {
            _highlightRefreshPending = false;
            if (!IsDisposed)
            {
                RefreshMatchHighlights();
            }
        }));
    }
}
