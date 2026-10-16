using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

public partial class HexTerminalControl : DpiAwareControl
{
    private class DataChunk
    {
        public bool IsBreak;

        public bool IsEcho;

        public List<byte> Bytes = new List<byte>();

        internal bool CanAppend(bool flag)
        {
            if (!IsBreak)
            {
                return this.IsEcho == flag;
            }
            return false;
        }
    }

    private int _bytesPerLine = 16;

    private DateTime _lastDataTime;

    private bool _lastWasEcho;

    private List<DataChunk> _chunks = new List<DataChunk>();

    public Brush EchoBrush;

    private DeviceTerminalSettings.HexTerminalSettings _settings;

    private bool _syncingSelection;

    private int _resizeTicks;

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

    public bool HideCaret
    {
        get
        {
            return termHex.HideCaret;
        }
        set
        {
            TerminalEmulatorBase terminalEmulatorBase = termHex;
            bool flag = (termText.HideCaret = value);
            terminalEmulatorBase.HideCaret = flag;
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
            foreach (DataChunk item in _chunks)
            {
                if (!item.IsBreak && !item.IsEcho && item.Bytes != null)
                {
                    list.AddRange(item.Bytes);
                }
            }
            return list.ToArray();
        }
    }

    public string HexText => termHex.Text;

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
    }

    public void AppendData(byte[] buffer, int value2, bool flag = false)
    {
        if (buffer == null)
        {
            return;
        }
        if (_lastDataTime != DateTime.MinValue && ((HexSettings.BreaksAroundEcho && flag != _lastWasEcho) || (HexSettings.TimeoutForAutoBreak > 0 && (DateTime.Now - _lastDataTime).TotalMilliseconds >= (double)HexSettings.TimeoutForAutoBreak)))
        {
            termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, flag: true);
            termText.MoveCursorBy(-termText.CursorPosition.X, 1, flag: true);
            _chunks.Add(new DataChunk
            {
                IsBreak = true
            });
        }
        if (_chunks.Count > 0 && _chunks[_chunks.Count - 1].CanAppend(flag))
        {
            for (int num = 0; num < value2; num++)
            {
                _chunks[_chunks.Count - 1].Bytes.Add(buffer[num]);
            }
        }
        else
        {
            DataChunk dataChunk = new DataChunk
            {
                IsEcho = flag
            };
            for (int num2 = 0; num2 < value2; num2++)
            {
                dataChunk.Bytes.Add(buffer[num2]);
            }
            _chunks.Add(dataChunk);
        }
        _lastDataTime = DateTime.Now;
        _lastWasEcho = flag;
        RenderData(buffer, value2, flag);
    }

    private void RenderData(byte[] buffer, int value2, bool flag)
    {
        try
        {
            int num = (termHex.CursorPosition.X + 1) / 3;
            StringBuilder stringBuilder = new StringBuilder(_bytesPerLine * 4);
            StringBuilder stringBuilder2 = new StringBuilder(_bytesPerLine * 2);
            if (flag)
            {
                termText.CurrentStyle = (termHex.CurrentStyle = new TerminalEmulatorBase.CellStyle
                {
                    Override = new TerminalEmulatorBase.CellStyle.ForegroundOverride
                    {
                        Brush = EchoBrush
                    }
                });
            }
            for (int num2 = 0; num2 < value2; num2++)
            {
                byte b2 = buffer[num2];
                if (num >= _bytesPerLine)
                {
                    termHex.WriteText(stringBuilder.ToString());
                    termText.WriteText(stringBuilder2.ToString());
                    termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, flag: true);
                    termText.MoveCursorBy(-termText.CursorPosition.X, 1, flag: true);
                    int length = (stringBuilder2.Length = 0);
                    stringBuilder.Length = length;
                    num = 0;
                }
                if (num != 0)
                {
                    stringBuilder.Append(" ");
                }
                num++;
                stringBuilder.AppendFormat("{0:x2}", b2);
                string text = "";
                if (b2 >= 32)
                {
                    text = Encoding.ASCII.GetString(new byte[1] { b2 });
                }
                if (text.Length == 1)
                {
                    stringBuilder2.Append(text);
                }
                else
                {
                    stringBuilder2.Append(".");
                }
            }
            termHex.WriteText(stringBuilder.ToString());
            termText.WriteText(stringBuilder2.ToString());
        }
        finally
        {
            termText.CurrentStyle = (termHex.CurrentStyle = TerminalEmulatorBase.CellStyle.Default);
        }
    }

    public void Clear()
    {
        _chunks.Clear();
        termHex.Reset();
        termText.Reset();
        _lastDataTime = DateTime.MinValue;
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
            int num = termHex.ScrollBarWidth;
            int num2 = termText.ScrollBarWidth;
            int num3 = Width - SystemInformation.VerticalScrollBarWidth - num - num2;
            termText.Width = num3 / 4 + num2 + SystemInformation.VerticalScrollBarWidth;
            termHex.Width = Width - termText.Width - 2;
        }
        else
        {
            termHex.Width = Width;
        }
        int num4 = termHex.TerminalSize.Width / 3;
        if (num4 < 1)
        {
            num4 = 1;
        }
        _bytesPerLine = Math.Min(num4, HexSettings.MaximumBytesPerLine);
    }

    private void SyncSelectionToText(ReadonlyTextBrowser readonlyTextBrowser, TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, bool flag)
    {
        if (_syncingSelection)
        {
            return;
        }
        try
        {
            _syncingSelection = true;
            TextDocument.TextPosition textPosition3 = termHex.SelectionStart;
            TextDocument.TextPosition textPosition4 = termHex.SelectionEnd;
            if (textPosition4 < textPosition3)
            {
                textPosition3.Column = (textPosition3.Column + 2) / 3;
                textPosition4.Column = (textPosition4.Column + 1) / 3;
            }
            else
            {
                textPosition3.Column = (textPosition3.Column + 1) / 3;
                textPosition4.Column = (textPosition4.Column + 2) / 3;
            }
            termText.SetSelection(((ReadonlyTextBrowser)termHex).HasSelection, new TextDocument.TextPosition(textPosition3.Column, textPosition3.Line));
            termText.SetSelectionEnd(new TextDocument.TextPosition(textPosition4.Column, textPosition4.Line));
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SyncSelectionToHex(ReadonlyTextBrowser readonlyTextBrowser, TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, bool flag)
    {
        if (_syncingSelection)
        {
            return;
        }
        try
        {
            _syncingSelection = true;
            TextDocument.TextPosition textPosition3 = termText.SelectionStart;
            TextDocument.TextPosition textPosition4 = termText.SelectionEnd;
            if (textPosition4 < textPosition3)
            {
                textPosition3.Column = Math.Max(0, textPosition3.Column * 3 - 1);
                textPosition4.Column *= 3;
            }
            else
            {
                textPosition3.Column *= 3;
                textPosition4.Column = Math.Max(0, textPosition4.Column * 3 - 1);
            }
            termHex.SetSelection(((ReadonlyTextBrowser)termText).HasSelection, new TextDocument.TextPosition(textPosition3.Column, textPosition3.Line));
            termHex.SetSelectionEnd(new TextDocument.TextPosition(textPosition4.Column, textPosition4.Line));
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
            source.CopySelection(flag: false);
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
        termHex.Reset();
        termText.Reset();
        foreach (DataChunk item in _chunks)
        {
            if (item.IsBreak)
            {
                termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, flag: true);
                termText.MoveCursorBy(-termText.CursorPosition.X, 1, flag: true);
            }
            else
            {
                RenderData(item.Bytes.ToArray(), item.Bytes.Count, item.IsEcho);
            }
        }
    }
}
