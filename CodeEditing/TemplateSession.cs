using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace SmarTTY.CodeEditing;

internal class TemplateSession : IDisposable
{
    public class TabStop
    {
        public CodeTemplate.TemplatePart Part;

        public TextDocument.TextPosition Start;

        public TextDocument.TextPosition End;

        public bool Modified;

        public int Line => Start.Line;

        public TabStop(CodeTemplate.TemplatePart templatePart, int line, int startCol, int endCol)
        {
            this.Part = templatePart;
            Start = new TextDocument.TextPosition(startCol, line);
            this.End = new TextDocument.TextPosition(endCol, line);
        }

        public bool HasSelection(CodeTemplate.PlaceholderSelection placeholderSelection)
        {
            return Part.HasSelection(placeholderSelection);
        }
    }

    private List<TabStop> _stops = new List<TabStop>();

    private CodeEditorPP _editor;

    private int _currentIndex = -1;

    private int _highlightId = -1;

    private bool _navigating;

    private bool _endPending;

    public HighlightStyle HighlightStyle = new HighlightStyle(Brushes.Transparent, new Pen(Color.FromArgb(150, Color.Blue), 1f), null);

    public TemplateSession(CodeEditorPP codeEditorPP, TextDocument.TextPosition textPosition, CodeTemplate codeTemplate)
    {
        _editor = codeEditorPP;
        if (_editor._templateSession != null)
        {
            _editor._templateSession.Dispose();
        }
        _editor._templateSession = this;
        string indent = codeEditorPP.GetIndentation(textPosition.Line);
        StringBuilder stringBuilder = new StringBuilder(1024);
        string lineText = codeEditorPP.GetLineOrNull(textPosition.Line);
        int identStartCol = textPosition.Column;
        while (--identStartCol >= 0 && "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(lineText[identStartCol]) != -1)
        {
        }
        if ((codeTemplate.InsertMode & CodeTemplate.TemplateInsertMode.AtCaret) != 0)
        {
            identStartCol = textPosition.Column - 1;
        }
        int lineIndex = textPosition.Line;
        int colIndex = identStartCol + 1;
        int lineIdx = 0;
        foreach (CodeTemplate.TemplateLine tmplLine in codeTemplate.Lines)
        {
            if (lineIdx++ > 0)
            {
                stringBuilder.Append(indent);
                stringBuilder.Append(new string('\t', tmplLine.Indent));
                colIndex = indent.Length + tmplLine.Indent;
            }
            foreach (CodeTemplate.TemplatePart tmplPart in tmplLine.Parts)
            {
                stringBuilder.Append(tmplPart.Text);
                if (tmplPart.IsTabStop)
                {
                    _stops.Add(new TabStop(tmplPart, lineIndex, colIndex, colIndex + tmplPart.Text.Length));
                }
                colIndex += tmplPart.Text.Length;
            }
            if (lineIdx != codeTemplate.Lines.Count)
            {
                stringBuilder.Append("\r\n");
                lineIndex++;
                colIndex = 0;
            }
        }
        _editor.ReplaceText(new TextDocument.TextPosition(identStartCol + 1, textPosition.Line), textPosition, select: false, stringBuilder.ToString());
        ((EditableTextBrowser)_editor).RangeChanged += this.OnRangeChanged;
        ((EditableTextBrowser)_editor).LineChanged += OnLineChanged;
        CodeEditorPP editor = _editor;
        editor._tabHandler = (CodeEditorPP.TabHandler)Delegate.Combine(editor._tabHandler, new CodeEditorPP.TabHandler(OnTab));
        MoveToStop(isShiftTab: false);
    }

    private void MoveToStop(bool isShiftTab)
    {
        int prevIndex = _currentIndex;
        if (_stops.Count == 0)
        {
            return;
        }
        if (!isShiftTab)
        {
            if (++_currentIndex >= _stops.Count)
            {
                _currentIndex = 0;
            }
        }
        else if (--_currentIndex < 0)
        {
            _currentIndex = _stops.Count - 1;
        }
        this._navigating = true;
        TabStop tabStop = _stops[_currentIndex];
        _editor.SetCaret(tabStop.Start);
        bool shouldSelect = !tabStop.Modified || tabStop.HasSelection(CodeTemplate.PlaceholderSelection.SelectText);
        if (tabStop.HasSelection(CodeTemplate.PlaceholderSelection.CaretOnly))
        {
            shouldSelect = false;
        }
        _editor.SetSelection(shouldSelect, tabStop.End);
        if (_currentIndex != prevIndex)
        {
            if (prevIndex != -1 && _stops[prevIndex].Part.Tag != null)
            {
                _editor.RaisePlaceholderChanged(_stops[prevIndex].Line, _stops[prevIndex].Start.Column, _stops[prevIndex].End.Column, _stops[prevIndex].Part.Tag, isLast: false);
            }
            if (tabStop.Part.Tag != null)
            {
                _editor.RaisePlaceholderChanged(tabStop.Line, tabStop.Start.Column, tabStop.End.Column, tabStop.Part.Tag, isLast: true);
            }
        }
        _highlightId = _editor.SetHighlight(_highlightId, _stops[_currentIndex].Start, _stops[_currentIndex].End, HighlightStyle);
        this._navigating = false;
        if (_endPending)
        {
            Dispose();
        }
    }

    private void OnTab(ref bool handled)
    {
        MoveToStop((Control.ModifierKeys & Keys.Shift) != 0);
        handled = true;
    }

    private void OnLineChanged(EditableTextBrowser editableTextBrowser, int lineIndex, int column, int oldLen, int newLen)
    {
        foreach (TabStop item in _stops)
        {
            if (lineIndex != item.Line)
            {
                continue;
            }
            int diff = newLen - oldLen;
            if (column + oldLen <= item.Start.Column)
            {
                item.Start.Column += diff;
                item.End.Column += diff;
            }
            else
            {
                if (column > item.End.Column)
                {
                    continue;
                }
                if (column < item.Start.Column)
                {
                    item.Start.Column = column;
                }
                if (column + oldLen > item.End.Column)
                {
                    item.End.Column = column + oldLen;
                }
                item.End.Column += diff;
                item.Modified = true;
            }
            if (item == _stops[this._currentIndex])
            {
                _highlightId = _editor.SetHighlight(_highlightId, item.Start, item.End, this.HighlightStyle);
            }
        }
    }

    private void OnRangeChanged(EditableTextBrowser editableTextBrowser, TextDocument.TextPosition startPos, TextDocument.TextPosition oldEndPos, TextDocument.TextPosition newEndPos)
    {
        Dispose();
    }

    public void RequestEnd()
    {
        if (_navigating)
        {
            _endPending = true;
        }
        else
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        ((EditableTextBrowser)_editor).RangeChanged -= OnRangeChanged;
        ((EditableTextBrowser)_editor).LineChanged -= OnLineChanged;
        CodeEditorPP codeEditorPP = _editor;
        codeEditorPP._tabHandler = (CodeEditorPP.TabHandler)Delegate.Remove(codeEditorPP._tabHandler, new CodeEditorPP.TabHandler(OnTab));
        _editor._templateSession = null;
        _editor.RemoveHighlight(_highlightId);
    }
}
