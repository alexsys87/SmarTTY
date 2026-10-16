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

        public TabStop(CodeTemplate.TemplatePart templatePart, int value2, int value3, int value4)
        {
            this.Part = templatePart;
            Start = new TextDocument.TextPosition(value3, value2);
            this.End = new TextDocument.TextPosition(value4, value2);
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
        string text = codeEditorPP.GetIndentation(textPosition.Line);
        StringBuilder stringBuilder = new StringBuilder(1024);
        string text2 = codeEditorPP.GetLineOrNull(textPosition.Line);
        int num = textPosition.Column;
        while (--num >= 0 && "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(text2[num]) != -1)
        {
        }
        if ((codeTemplate.InsertMode & CodeTemplate.TemplateInsertMode.AtCaret) != 0)
        {
            num = textPosition.Column - 1;
        }
        int num2 = textPosition.Line;
        int num3 = num + 1;
        int num4 = 0;
        foreach (CodeTemplate.TemplateLine item in codeTemplate.Lines)
        {
            if (num4++ > 0)
            {
                stringBuilder.Append(text);
                stringBuilder.Append(new string('\t', item.Indent));
                num3 = text.Length + item.Indent;
            }
            foreach (CodeTemplate.TemplatePart item2 in item.Parts)
            {
                stringBuilder.Append(item2.Text);
                if (item2.IsTabStop)
                {
                    _stops.Add(new TabStop(item2, num2, num3, num3 + item2.Text.Length));
                }
                num3 += item2.Text.Length;
            }
            if (num4 != codeTemplate.Lines.Count)
            {
                stringBuilder.Append("\r\n");
                num2++;
                num3 = 0;
            }
        }
        _editor.ReplaceText(new TextDocument.TextPosition(num + 1, textPosition.Line), textPosition, flag: false, stringBuilder.ToString());
        ((EditableTextBrowser)_editor).RangeChanged += this.OnRangeChanged;
        ((EditableTextBrowser)_editor).LineChanged += OnLineChanged;
        CodeEditorPP codeEditorPP2 = _editor;
        codeEditorPP2._tabHandler = (CodeEditorPP.TabHandler)Delegate.Combine(codeEditorPP2._tabHandler, new CodeEditorPP.TabHandler(OnTab));
        MoveToStop(flag2: false);
    }

    private void MoveToStop(bool flag2)
    {
        int num = _currentIndex;
        if (_stops.Count == 0)
        {
            return;
        }
        if (!flag2)
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
        bool flag = !tabStop.Modified || tabStop.HasSelection(CodeTemplate.PlaceholderSelection.SelectText);
        if (tabStop.HasSelection(CodeTemplate.PlaceholderSelection.CaretOnly))
        {
            flag = false;
        }
        _editor.SetSelection(flag, tabStop.End);
        if (_currentIndex != num)
        {
            if (num != -1 && _stops[num].Part.Tag != null)
            {
                _editor.RaisePlaceholderChanged(_stops[num].Line, _stops[num].Start.Column, _stops[num].End.Column, _stops[num].Part.Tag, flag: false);
            }
            if (tabStop.Part.Tag != null)
            {
                _editor.RaisePlaceholderChanged(tabStop.Line, tabStop.Start.Column, tabStop.End.Column, tabStop.Part.Tag, flag: true);
            }
        }
        _highlightId = _editor.SetHighlight(_highlightId, _stops[_currentIndex].Start, _stops[_currentIndex].End, HighlightStyle);
        this._navigating = false;
        if (_endPending)
        {
            Dispose();
        }
    }

    private void OnTab(ref bool flag)
    {
        MoveToStop((Control.ModifierKeys & Keys.Shift) != 0);
        flag = true;
    }

    private void OnLineChanged(EditableTextBrowser editableTextBrowser, int value2, int value3, int value4, int value5)
    {
        foreach (TabStop item in _stops)
        {
            if (value2 != item.Line)
            {
                continue;
            }
            int num = value5 - value4;
            if (value3 + value4 <= item.Start.Column)
            {
                item.Start.Column += num;
                item.End.Column += num;
            }
            else
            {
                if (value3 > item.End.Column)
                {
                    continue;
                }
                if (value3 < item.Start.Column)
                {
                    item.Start.Column = value3;
                }
                if (value3 + value4 > item.End.Column)
                {
                    item.End.Column = value3 + value4;
                }
                item.End.Column += num;
                item.Modified = true;
            }
            if (item == _stops[this._currentIndex])
            {
                _highlightId = _editor.SetHighlight(_highlightId, item.Start, item.End, this.HighlightStyle);
            }
        }
    }

    private void OnRangeChanged(EditableTextBrowser editableTextBrowser, TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, TextDocument.TextPosition textPosition3)
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
