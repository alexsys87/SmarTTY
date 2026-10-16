using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

public class CodeTemplate
{
    public enum PlaceholderSelection
    {
        Default,
        SelectText,
        CaretOnly
    }

    public enum TemplateInsertMode
    {
        ReplaceWord,
        AtCaret
    }

    public class TemplatePart
    {
        public readonly string Text;

        public readonly bool IsTabStop;

        public readonly PlaceholderSelection Selection;

        public object Tag;

        public TemplatePart(string text, bool flag, PlaceholderSelection placeholderSelection, object obj)
        {
            this.Text = text;
            this.IsTabStop = flag;
            this.Selection = placeholderSelection;
            Tag = obj;
        }

        public bool HasSelection(PlaceholderSelection placeholderSelection)
        {
            return (Selection & placeholderSelection) == placeholderSelection;
        }
    }

    public class TabStopPart : TemplatePart
    {
        public TabStopPart(string text, PlaceholderSelection placeholderSelection, object obj)
            : base(text, flag: true, placeholderSelection, obj)
        {
        }

        public TabStopPart(string text)
            : this(text, PlaceholderSelection.Default, null)
        {
        }

        public TabStopPart(string text, object obj)
            : this(text, PlaceholderSelection.Default, obj)
        {
        }
    }

    public class LiteralPart : TemplatePart
    {
        public LiteralPart(string text, PlaceholderSelection placeholderSelection, object obj)
            : base(text, flag: false, placeholderSelection, obj)
        {
        }

        public LiteralPart(string text)
            : this(text, PlaceholderSelection.Default, null)
        {
        }

        public LiteralPart(string text, object obj)
            : this(text, PlaceholderSelection.Default, obj)
        {
        }
    }

    public class TemplateLine
    {
        public readonly int Indent;

        public List<TemplatePart> Parts;

        public TemplateLine(int value2)
        {
            this.Indent = value2;
            Parts = new List<TemplatePart>();
        }

        public TemplateLine(int value2, params TemplatePart[] items)
        {
            this.Indent = value2;
            this.Parts = new List<TemplatePart>(items);
        }

        public TemplateLine(int value2, params string[] items)
        {
            this.Indent = value2;
            this.Parts = new List<TemplatePart>();
            foreach (string text in items)
            {
                if (text[0] == '+')
                {
                    this.Parts.Add(new TabStopPart(text.Substring(1)));
                    continue;
                }
                if (text[0] == '-')
                {
                    this.Parts.Add(new LiteralPart(text.Substring(1)));
                    continue;
                }
                throw new ArgumentException();
            }
        }
    }

    public List<TemplateLine> Lines = new List<TemplateLine>();

    public TemplateInsertMode InsertMode;

    public CodeTemplate(params TemplateLine[] items)
    {
        Lines = new List<TemplateLine>(items);
    }

    internal void Insert(CodeEditorPP codeEditorPP, TextDocument.TextPosition textPosition)
    {
        new TemplateSession(codeEditorPP, textPosition, this);
    }
}
