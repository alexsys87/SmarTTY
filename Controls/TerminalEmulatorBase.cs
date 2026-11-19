using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Rendering;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

public partial class TerminalEmulatorBase : TextEditorControl, ITerminalScreenView
{
    private class TimestampMargin : IMargin
    {
        private TerminalEmulatorBase _terminal;

        private StringFormat _format;

        public TimestampMargin(TerminalEmulatorBase terminalEmulatorBase)
        {
            _terminal = terminalEmulatorBase;
            _format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center
            };
        }

        public void Paint(Graphics graphics, Rectangle rectangle, SizeF sizeF, int row, int topLine, int visibleRows, ReadonlyTextBrowser readonlyTextBrowser)
        {
            ScreenBuffer screenBuffer = this._terminal.ActiveBuffer;
            if (screenBuffer == null)
            {
                return;
            }
            using (SolidBrush brush = new SolidBrush(this._terminal.ForeColor))
            {
                for (int rowOffset = 0; rowOffset < visibleRows; rowOffset++)
                {
                    LineInfo lineInfo = screenBuffer.Lines.GetLine(topLine + rowOffset);
                    if (lineInfo != null && !(lineInfo.Timestamp == DateTime.MinValue))
                    {
                        int y = (int)((float)rectangle.Top + (float)rowOffset * sizeF.Height);
                        int bottom = y + (int)sizeF.Height;
                        string text = lineInfo.Timestamp.ToString(this._terminal.TimestampFormat);
                        graphics.DrawString(text, readonlyTextBrowser.Font, brush, Rectangle.FromLTRB(rectangle.Left, y, rectangle.Right, bottom), this._format);
                    }
                }
            }
            using Pen pen = new Pen(Color.FromArgb(128, this._terminal.ForeColor), 1f);
            graphics.DrawLine(pen, rectangle.Right - 5, rectangle.Top, rectangle.Right - 5, rectangle.Bottom);
        }

        public int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int line)
        {
            return (int)Math.Ceiling(graphics.MeasureString(DateTime.Now.ToString(_terminal.TimestampFormat), readonlyTextBrowser.Font).Width) + 10;
        }

        public bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int line, int row, SizeF sizeF, int column)
        {
            return true;
        }
    }

    private string _title;

    private TerminalScreen _screen;

    private SolidBrush _defaultForeBrush = new SolidBrush(Color.White);

    private SolidBrush _defaultBackBrush = new SolidBrush(Color.Black);

    // The rule that turns the style of a cell into its colours (the palette is the user's, the defaults are those of the control).
    private readonly TerminalColorScheme _colorScheme = CreateColorScheme(ConsolePalette.PuTTY);

    // The brushes of the colours of the rule, by colour, made on first use.
    private readonly Dictionary<uint, Brush> _schemeBrushes = new Dictionary<uint, Brush>();

    public Brush InactiveBrush = Brushes.DarkGray;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CellStyle CurrentStyle
    {
        get => _screen.CurrentStyle;
        set => _screen.CurrentStyle = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TabStops Tabs
    {
        get => _screen.Tabs;
        set => _screen.Tabs = value;
    }

    public bool CopyOnSelect = true;

    public string TimestampFormat = "HH:mm:ss";

    public bool AlwaysTrackLines;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CaretWhenUnfocused
    {
        get
        {
            return ShowCaretWhenUnfocused;
        }
        set
        {
            ShowCaretWhenUnfocused = value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get
        {
            return _title;
        }
        set
        {
            bool titleChanged = _title != value;
            _title = value;
            if (titleChanged && TitleChanged != null)
            {
                TitleChanged(this, EventArgs.Empty);
            }
        }
    }

    public Size TerminalSize => new Size(_visibleColumns, ((ReadonlyTextBrowser)this)._visibleRows);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Point CursorPosition
    {
        get => _screen.Cursor.ToPoint();
        set => _screen.Cursor = value.ToGridPoint();
    }

    protected virtual bool UnusedFlag => false;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color NormalTextColor
    {
        get
        {
            return _defaultForeBrush.Color;
        }
        set
        {
            _defaultForeBrush = new SolidBrush(value);
            Redraw();
        }
    }

    public override Color BackColor
    {
        get
        {
            return base.BackColor;
        }
        set
        {
            base.BackColor = value;
            _defaultBackBrush = new SolidBrush(value);
        }
    }

    // Null while the control is being built (the buffers are made at the end of the constructor).
    protected ScreenBuffer ActiveBuffer => _screen?.ActiveBuffer;

    internal TerminalScreen Screen => _screen;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ActiveBufferIndex
    {
        get => _screen.ActiveBufferIndex;
        set => _screen.ActiveBufferIndex = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ScrollbackLimit
    {
        get => _screen.ScrollbackLimit;
        set => _screen.ScrollbackLimit = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoWrap
    {
        get => _screen.AutoWrap;
        set
        {
            _screen.AutoWrap = value;
            base.HorizontalScrollBarVisible = !value;
            UpdateLayout();
        }
    }

    public int ScrollBarWidth => Width - TextArea.Width;

    public event EventHandler TitleChanged;

    public TerminalEmulatorBase()
    {
        _screen = new TerminalScreen(this);
        InitializeComponent();
        CaretWhenUnfocused = true;
        _allowPartialRows = false;
        _screen.CreateBuffers();
        ActiveBufferIndex = 0;
        ClearMargins();
        AddMargin(new TimestampMargin(this));
        HideMargins = true;
    }

    protected void UpdateCaret()
    {
        _screen.UpdateCaret();
    }

    /// <summary>The line of the text the cursor is in (the rows scrolled off the screen are counted).</summary>
    public int CursorDocumentLine => _screen.CursorDocumentLine;

    protected TextDocument.TextPosition ToDocumentPosition(Point point)
    {
        return _screen.ToDocumentPosition(point.ToGridPoint());
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ActiveBuffer != null)
        {
            _screen.Resized();
        }
    }

    public void MoveCursorBy(int deltaX, int deltaY, bool scroll = false)
    {
        _screen.MoveCursorBy(deltaX, deltaY, scroll);
    }

    /// <summary>
    /// Writes one line of terminal diagnostics (the control characters seen and, through <c>EscapeTrace</c>,
    /// what every escape sequence meant). Only a Debug build calls this: the attribute removes every call from a
    /// Release build, so nothing is executed or allocated there. The lines go to the debugger output
    /// (<c>Debug.WriteLine</c>), i.e. the Visual Studio Output window, DebugView or a debugger; a Release build
    /// that runs without a debugger therefore stays completely unaffected.
    /// </summary>
    [Conditional("DEBUG")]
    protected void Trace(string text)
    {
        Debug.WriteLine(text);
    }

    public void WriteText(string text, bool moveCursor = true)
    {
        _screen.WriteText(text, moveCursor);
    }

    internal virtual ILinkScanner CreateLinkScanner()
    {
        return null;
    }


    protected void ResetLinkTracking()
    {
        _screen.ResetLinkTracking();
    }

    public void SetPalette(ConsolePalette consolePalette)
    {
        ApplyPalette(_colorScheme, consolePalette);
    }

    private static TerminalColorScheme CreateColorScheme(ConsolePalette consolePalette)
    {
        TerminalColorScheme scheme = new TerminalColorScheme();
        ApplyPalette(scheme, consolePalette);
        return scheme;
    }

    private static void ApplyPalette(TerminalColorScheme scheme, ConsolePalette consolePalette)
    {
        for (int index = 0; index < 8; index++)
        {
            if (index < consolePalette.NormalColors.Length)
            {
                scheme.SetPaletteColor(index, bright: false, ((Color)consolePalette.NormalColors[index]).ToRgb());
            }
            if (index < consolePalette.BrightColors.Length)
            {
                scheme.SetPaletteColor(index, bright: true, ((Color)consolePalette.BrightColors[index]).ToRgb());
            }
        }
    }

    // The brush of a colour of the colour rule (null: no colour, the default of the view); made on first use.
    private Brush GetSchemeBrush(RgbColor? color)
    {
        if (!color.HasValue)
        {
            return null;
        }
        if (!_schemeBrushes.TryGetValue(color.Value.Argb, out Brush brush))
        {
            if (_schemeBrushes.Count > 4096)
            {
                _schemeBrushes.Clear();
            }
            brush = new SolidBrush(color.Value.ToColor());
            _schemeBrushes[color.Value.Argb] = brush;
        }
        return brush;
    }

    // The colours that the rule takes from the control: the text colour, the colour of bold text without a colour, the background.
    private void SyncColorScheme()
    {
        if (_defaultColors[0].Foreground is SolidBrush text)
        {
            _colorScheme.DefaultForeground = text.Color.ToRgb();
        }
        _colorScheme.BoldDefaultForeground = _defaultForeBrush.Color.ToRgb();
        _colorScheme.DefaultBackground = _defaultBackBrush.Color.ToRgb();
    }

    // The colours of a cell come from the rule that the WPF view uses too (Terminal/Rendering/TerminalColorScheme.cs), so both paint the same.
    private ColorSpan CreateColorSpan(int start, int end, CellStyle cellStyle)
    {
        ResolvedCellColors colors = _colorScheme.Resolve(cellStyle);
        return new ColorSpan
        {
            Foreground = GetSchemeBrush(colors.Foreground),
            Background = GetSchemeBrush(colors.Background),
            Start = start,
            End = end,
            Underline = colors.Underline
        };
    }

    protected override ColorSpan[] GetLineColorSpans(int line)
    {
        LineInfo lineInfo = ActiveBuffer.Lines.GetLine(line);
        if (lineInfo == null)
        {
            return null;
        }
        if (lineInfo.IsDimmed)
        {
            return new ColorSpan[1]
            {
                new ColorSpan
                {
                    Start = 0,
                    End = int.MaxValue,
                    Foreground = InactiveBrush
                }
            };
        }
        if (lineInfo.Cells == null || lineInfo.Cells.Length == 0)
        {
            return null;
        }
        SyncColorScheme();
        CellStyle cellStyle = default;
        int spanStart = 0;
        List<ColorSpan> list = null;
        for (int column = 0; column < lineInfo.Cells.Length; column++)
        {
            if (cellStyle.StyleEquals(lineInfo.Cells[column]))
            {
                continue;
            }
            if (!cellStyle.IsDefault)
            {
                if (list == null)
                {
                    list = new List<ColorSpan>();
                }
                list.Add(CreateColorSpan(spanStart, column, cellStyle));
            }
            cellStyle = lineInfo.Cells[column];
            spanStart = column;
        }
        if (!cellStyle.IsDefault)
        {
            if (list == null)
            {
                list = new List<ColorSpan>();
            }
            list.Add(CreateColorSpan(spanStart, lineInfo.Cells.Length, cellStyle));
        }
        return list?.ToArray();
    }

    /// <summary>
    /// "Clear" of the context menu: removes the scrollback and everything on the screen above the line of the cursor, which
    /// moves to the top together with its prompt. Nothing is sent to the remote side, so it works whatever is running there.
    /// The alternate screen (vi, mc, htop ...) belongs to the program and is left alone.
    /// </summary>
    public virtual void ClearOutput()
    {
        _screen.ClearOutput();
    }

    protected Point HitTestCell(Point point)
    {
        TextDocument.TextPosition textPosition = HitTest(point);
        if (!textPosition.IsValid)
        {
            return default;
        }
        return new Point(textPosition.Column, textPosition.Line - ActiveBuffer.FirstRow);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            base.OnPaint(e);
        }
        catch
        {
        }
    }

    public virtual void Reset()
    {
        _screen.Reset();
    }

    public string GetLineText(int line)
    {
        return _screen.GetLineText(line);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ((ReadonlyTextBrowser)this).HasSelection && CopyOnSelect)
        {
            CopySelection();
        }
    }

    private bool IsLineWrapped(int line)
    {
        ScreenBuffer screenBuffer = ActiveBuffer;
        return screenBuffer != null && screenBuffer.Lines?.GetLine(line)?.IsWrapped == true;
    }

    private string GetLineBreak(int line)
    {
        if (IsLineWrapped(line))
        {
            return "";
        }
        return "\r\n";
    }

    protected override string GetSelectedText(TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        return base.EditableDocument.GetRangeText(start, end, GetLineBreak);
    }

    protected override void SelectTerminalLine(TextDocument.TextPosition textPosition)
    {
        if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
        {
            SelectVisibleScreen();
            return;
        }
        // A line the terminal wrapped continues on the following document lines without a line break between them,
        // so the click selects the whole logical line even when it lands on one of the continuation rows.
        int firstLine = textPosition.Line;
        while (firstLine > 0 && IsLineWrapped(firstLine - 1))
        {
            firstLine--;
        }
        int lastLine = textPosition.Line;
        while (lastLine + 1 < base.EditableDocument.LineCount && IsLineWrapped(lastLine))
        {
            lastLine++;
        }
        SelectLineRange(firstLine, lastLine);
    }

    private void SelectVisibleScreen()
    {
        if (base.EditableDocument.LineCount == 0)
        {
            return;
        }
        int firstLine = YToLine(TextArea.Top);
        int lastLine = YToLine(TextArea.Bottom - 1);
        if (firstLine < 0 || lastLine < 0)
        {
            return;
        }
        lastLine = Math.Min(lastLine, GetScreenSelectionLastLine());
        if (lastLine < firstLine)
        {
            // Only the command line is visible: there is no output on the screen to select.
            SetSelection(select: false, SelectionStart);
            return;
        }
        SelectLineRange(firstLine, lastLine);
    }

    // The last line of the visible screen that belongs to the console output. Controls that keep the editable
    // command line at the bottom of the screen exclude it, so a copy does not start or end with the prompt.
    protected virtual int GetScreenSelectionLastLine()
    {
        return int.MaxValue;
    }

    public new void SelectAllText()
    {
        if (base.EditableDocument.LineCount != 0)
        {
            SetSelectionEnd(new TextDocument.TextPosition(base.EditableDocument.GetLineText(base.EditableDocument.LineCount - 1).Length, base.EditableDocument.LineCount - 1));
            SetSelection(select: true, new TextDocument.TextPosition(0, 0));
        }
    }


    public void ArchiveScreen()
    {
        _screen.ArchiveScreen();
    }

    public virtual void BeginNewSession()
    {
        ArchiveScreen();
    }
}
