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

        public TemplatePart(string text, bool isTabStop, PlaceholderSelection selection, object tag)
        {
            this.Text = text;
            this.IsTabStop = isTabStop;
            this.Selection = selection;
            Tag = tag;
        }

        public bool HasSelection(PlaceholderSelection placeholderSelection)
        {
            return (Selection & placeholderSelection) == placeholderSelection;
        }
    }

    public class TabStopPart : TemplatePart
    {
        public TabStopPart(string text, PlaceholderSelection placeholderSelection, object tag)
            : base(text, isTabStop: true, placeholderSelection, tag)
        {
        }

        public TabStopPart(string text)
            : this(text, PlaceholderSelection.Default, null)
        {
        }

        public TabStopPart(string text, object tag)
            : this(text, PlaceholderSelection.Default, tag)
        {
        }
    }

    public class LiteralPart : TemplatePart
    {
        public LiteralPart(string text, PlaceholderSelection placeholderSelection, object tag)
            : base(text, isTabStop: false, placeholderSelection, tag)
        {
        }

        public LiteralPart(string text)
            : this(text, PlaceholderSelection.Default, null)
        {
        }

        public LiteralPart(string text, object tag)
            : this(text, PlaceholderSelection.Default, tag)
        {
        }
    }

    public class TemplateLine
    {
        public readonly int Indent;

        public List<TemplatePart> Parts;

        public TemplateLine(int indent)
        {
            this.Indent = indent;
            Parts = new List<TemplatePart>();
        }

        public TemplateLine(int indent, params TemplatePart[] parts)
        {
            this.Indent = indent;
            this.Parts = new List<TemplatePart>(parts);
        }

        public TemplateLine(int indent, params string[] partStrings)
        {
            this.Indent = indent;
            this.Parts = new List<TemplatePart>();
            foreach (string str in partStrings)
            {
                if (str[0] == '+')
                {
                    this.Parts.Add(new TabStopPart(str.Substring(1)));
                    continue;
                }
                if (str[0] == '-')
                {
                    this.Parts.Add(new LiteralPart(str.Substring(1)));
                    continue;
                }
                throw new ArgumentException();
            }
        }
    }

    public List<TemplateLine> Lines = new List<TemplateLine>();

    public TemplateInsertMode InsertMode;

    public CodeTemplate(params TemplateLine[] lines)
    {
        Lines = new List<TemplateLine>(lines);
    }

    internal void Insert(CodeEditorPP codeEditorPP, TextDocument.TextPosition textPosition)
    {
        new TemplateSession(codeEditorPP, textPosition, this);
    }
}
