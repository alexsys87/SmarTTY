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

        internal bool CanAppend(bool isEcho)
        {
            if (!IsBreak)
            {
                return this.IsEcho == isEcho;
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
            bool hideCaret = (termText.HideCaret = value);
            terminalEmulatorBase.HideCaret = hideCaret;
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

    public void AppendData(byte[] buffer, int count, bool isEcho = false)
    {
        if (buffer == null)
        {
            return;
        }
        if (_lastDataTime != DateTime.MinValue && ((HexSettings.BreaksAroundEcho && isEcho != _lastWasEcho) || (HexSettings.TimeoutForAutoBreak > 0 && (DateTime.Now - _lastDataTime).TotalMilliseconds >= (double)HexSettings.TimeoutForAutoBreak)))
        {
            termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, scroll: true);
            termText.MoveCursorBy(-termText.CursorPosition.X, 1, scroll: true);
            _chunks.Add(new DataChunk
            {
                IsBreak = true
            });
        }
        if (_chunks.Count > 0 && _chunks[_chunks.Count - 1].CanAppend(isEcho))
        {
            for (int byteIndex = 0; byteIndex < count; byteIndex++)
            {
                _chunks[_chunks.Count - 1].Bytes.Add(buffer[byteIndex]);
            }
        }
        else
        {
            DataChunk dataChunk = new DataChunk
            {
                IsEcho = isEcho
            };
            for (int chunkByteIndex = 0; chunkByteIndex < count; chunkByteIndex++)
            {
                dataChunk.Bytes.Add(buffer[chunkByteIndex]);
            }
            _chunks.Add(dataChunk);
        }
        _lastDataTime = DateTime.Now;
        _lastWasEcho = isEcho;
        RenderData(buffer, count, isEcho);
    }

    private void RenderData(byte[] buffer, int count, bool isEcho)
    {
        try
        {
            int currentColumn = (termHex.CursorPosition.X + 1) / 3;
            StringBuilder stringBuilder = new StringBuilder(_bytesPerLine * 4);
            StringBuilder hexTextBuilder = new StringBuilder(_bytesPerLine * 2);
            if (isEcho)
            {
                termText.CurrentStyle = (termHex.CurrentStyle = new TerminalEmulatorBase.CellStyle
                {
                    Override = new TerminalEmulatorBase.CellStyle.ForegroundOverride
                    {
                        Brush = EchoBrush
                    }
                });
            }
            for (int byteIndex = 0; byteIndex < count; byteIndex++)
            {
                byte currentByte = buffer[byteIndex];
                if (currentColumn >= _bytesPerLine)
                {
                    termHex.WriteText(stringBuilder.ToString());
                    termText.WriteText(hexTextBuilder.ToString());
                    termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, scroll: true);
                    termText.MoveCursorBy(-termText.CursorPosition.X, 1, scroll: true);
                    int length = (hexTextBuilder.Length = 0);
                    stringBuilder.Length = length;
                    currentColumn = 0;
                }
                if (currentColumn != 0)
                {
                    stringBuilder.Append(" ");
                }
                currentColumn++;
                stringBuilder.AppendFormat("{0:x2}", currentByte);
                string text = "";
                if (currentByte >= 32)
                {
                    text = Encoding.ASCII.GetString(new byte[1] { currentByte });
                }
                if (text.Length == 1)
                {
                    hexTextBuilder.Append(text);
                }
                else
                {
                    hexTextBuilder.Append(".");
                }
            }
            termHex.WriteText(stringBuilder.ToString());
            termText.WriteText(hexTextBuilder.ToString());
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
        _bytesPerLine = Math.Min(bytesPerLineLimit, HexSettings.MaximumBytesPerLine);
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
            if (selectionEnd < selectionStart)
            {
                selectionStart.Column = (selectionStart.Column + 2) / 3;
                selectionEnd.Column = (selectionEnd.Column + 1) / 3;
            }
            else
            {
                selectionStart.Column = (selectionStart.Column + 1) / 3;
                selectionEnd.Column = (selectionEnd.Column + 2) / 3;
            }
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
            if (selectionEnd < selectionStart)
            {
                selectionStart.Column = Math.Max(0, selectionStart.Column * 3 - 1);
                selectionEnd.Column *= 3;
            }
            else
            {
                selectionStart.Column *= 3;
                selectionEnd.Column = Math.Max(0, selectionEnd.Column * 3 - 1);
            }
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
        termHex.Reset();
        termText.Reset();
        foreach (DataChunk item in _chunks)
        {
            if (item.IsBreak)
            {
                termHex.MoveCursorBy(-termHex.CursorPosition.X, 1, scroll: true);
                termText.MoveCursorBy(-termText.CursorPosition.X, 1, scroll: true);
            }
            else
            {
                RenderData(item.Bytes.ToArray(), item.Bytes.Count, item.IsEcho);
            }
        }
    }
}
