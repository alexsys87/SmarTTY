using System.Collections.Generic;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>What a piece of the text of a Markdown block is.</summary>
public enum MarkdownInlineKind
{
    /// <summary>Plain text (<see cref="MarkdownInline.Text"/>).</summary>
    Text,

    /// <summary>A code span: <c>`code`</c> (<see cref="MarkdownInline.Text"/>).</summary>
    Code,

    /// <summary><c>*emphasis*</c> or <c>_emphasis_</c> (<see cref="MarkdownInline.Children"/>).</summary>
    Emphasis,

    /// <summary><c>**strong**</c> or <c>__strong__</c> (<see cref="MarkdownInline.Children"/>).</summary>
    Strong,

    /// <summary><c>~~struck~~</c> (<see cref="MarkdownInline.Children"/>).</summary>
    Strikethrough,

    /// <summary>A link: the text in <see cref="MarkdownInline.Children"/>, the target in <see cref="MarkdownInline.Url"/>.</summary>
    Link,

    /// <summary>An image: the alternative text in <see cref="MarkdownInline.Text"/>, the picture in <see cref="MarkdownInline.Url"/>.</summary>
    Image,

    /// <summary>A line break inside a paragraph that is shown as a blank.</summary>
    SoftBreak,

    /// <summary>A line break that is shown (two blanks or a backslash at the end of a line, <c>&lt;br&gt;</c>).</summary>
    LineBreak,

    /// <summary>An HTML tag that shows nothing (<see cref="MarkdownInline.Text"/> is the tag as written).</summary>
    Html
}

/// <summary>A piece of the text of a block: text, a code span, emphasis, a link, an image or a line break.</summary>
public sealed class MarkdownInline
{
    public MarkdownInline(MarkdownInlineKind kind, string text = null)
    {
        Kind = kind;
        Text = text;
    }

    public MarkdownInlineKind Kind { get; }

    /// <summary>The text of plain text, of a code span or of an HTML tag, and the alternative text of an image.</summary>
    public string Text { get; internal set; }

    /// <summary>The target of a link or an image, as written (backslash escapes and entities resolved).</summary>
    public string Url { get; internal set; }

    /// <summary>The title of a link or an image, or null.</summary>
    public string Title { get; internal set; }

    /// <summary>The content of emphasis, strong, struck text and a link; empty for the others.</summary>
    public List<MarkdownInline> Children { get; } = new List<MarkdownInline>();

    // A run of delimiters or an opening bracket while the inlines are parsed: never merged with the text next to it.
    internal bool IsMarker;

    // How deep emphasis and links are nested in it (0 for text): the parser keeps it under a limit.
    internal int Depth;

    /// <summary>The text that the inlines show, without their formatting (line breaks as blanks).</summary>
    public static string PlainText(IEnumerable<MarkdownInline> inlines)
    {
        StringBuilder text = new StringBuilder();
        AppendPlainText(inlines, text);
        return text.ToString();
    }

    private static void AppendPlainText(IEnumerable<MarkdownInline> inlines, StringBuilder text)
    {
        foreach (MarkdownInline inline in inlines)
        {
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Text:
                case MarkdownInlineKind.Code:
                case MarkdownInlineKind.Image:
                    text.Append(inline.Text);
                    break;
                case MarkdownInlineKind.SoftBreak:
                case MarkdownInlineKind.LineBreak:
                    text.Append(' ');
                    break;
                case MarkdownInlineKind.Html:
                    break;
                default:
                    AppendPlainText(inline.Children, text);
                    break;
            }
        }
    }
}

/// <summary>What a block of a Markdown document is.</summary>
public enum MarkdownBlockKind
{
    /// <summary>A paragraph (<see cref="MarkdownBlock.Inlines"/>).</summary>
    Paragraph,

    /// <summary>A heading of <see cref="MarkdownBlock.Level"/> 1 to 6 (<see cref="MarkdownBlock.Inlines"/>, <see cref="MarkdownBlock.Anchor"/>).</summary>
    Heading,

    /// <summary>A fenced or indented code block (<see cref="MarkdownBlock.Text"/>, the language in <see cref="MarkdownBlock.Info"/>).</summary>
    Code,

    /// <summary>A block quote (<see cref="MarkdownBlock.Children"/>).</summary>
    Quote,

    /// <summary>A bulleted or numbered list (<see cref="MarkdownBlock.Items"/>).</summary>
    List,

    /// <summary>A table (<see cref="MarkdownBlock.Header"/>, <see cref="MarkdownBlock.Rows"/>, <see cref="MarkdownBlock.Alignments"/>).</summary>
    Table,

    /// <summary>A horizontal rule (<c>---</c>, <c>***</c>).</summary>
    Rule,

    /// <summary>An HTML block, shown as its text without the tags (<see cref="MarkdownBlock.Inlines"/>).</summary>
    Html
}

/// <summary>How the cells of a column of a table are aligned.</summary>
public enum MarkdownAlignment
{
    None,
    Left,
    Center,
    Right
}

/// <summary>A block of a Markdown document with the lines of the source it was made of.</summary>
public sealed class MarkdownBlock
{
    public MarkdownBlock(MarkdownBlockKind kind, int line, int endLine)
    {
        Kind = kind;
        Line = line;
        EndLine = endLine;
    }

    public MarkdownBlockKind Kind { get; }

    /// <summary>The first line of the source of the block, counted from 0.</summary>
    public int Line { get; }

    /// <summary>The last line of the source of the block (included), counted from 0.</summary>
    public int EndLine { get; internal set; }

    /// <summary>The level of a heading, 1 to 6.</summary>
    public int Level { get; internal set; }

    /// <summary>The text of a paragraph, a heading or an HTML block.</summary>
    public List<MarkdownInline> Inlines { get; internal set; } = new List<MarkdownInline>();

    /// <summary>The name a link to a heading uses (<c>#getting-started</c>), as GitHub makes it.</summary>
    public string Anchor { get; internal set; }

    /// <summary>The text of a code block: its lines with LF between them.</summary>
    public string Text { get; internal set; }

    /// <summary>The info string of a fenced code block (its first word is the language), or empty.</summary>
    public string Info { get; internal set; } = "";

    /// <summary>The blocks of a quote.</summary>
    public List<MarkdownBlock> Children { get; } = new List<MarkdownBlock>();

    /// <summary>The items of a list.</summary>
    public List<MarkdownListItem> Items { get; } = new List<MarkdownListItem>();

    /// <summary>A numbered list (otherwise bulleted).</summary>
    public bool Ordered { get; internal set; }

    /// <summary>The number of the first item of a numbered list.</summary>
    public int Start { get; internal set; } = 1;

    /// <summary>No blank line separates the items or the blocks of an item: the paragraphs are shown without space between them.</summary>
    public bool Tight { get; internal set; } = true;

    /// <summary>The alignment of each column of a table.</summary>
    public List<MarkdownAlignment> Alignments { get; } = new List<MarkdownAlignment>();

    /// <summary>The cells of the header row of a table.</summary>
    public List<List<MarkdownInline>> Header { get; } = new List<List<MarkdownInline>>();

    /// <summary>The rows of a table below the header, each with as many cells as the header.</summary>
    public List<List<List<MarkdownInline>>> Rows { get; } = new List<List<List<MarkdownInline>>>();

    // While the document is parsed: the text the inlines are made from (a paragraph, a heading, an HTML block) and of the cells of a table.
    internal string RawText;

    internal List<string> RawHeader;

    internal List<List<string>> RawRows;
}

/// <summary>An item of a Markdown list.</summary>
public sealed class MarkdownListItem
{
    public MarkdownListItem(int line)
    {
        Line = line;
    }

    /// <summary>The line of the source the item starts at, counted from 0.</summary>
    public int Line { get; }

    /// <summary>The blocks of the item.</summary>
    public List<MarkdownBlock> Blocks { get; } = new List<MarkdownBlock>();

    /// <summary>A task item: true for <c>[x]</c>, false for <c>[ ]</c>; null for an item that is not a task.</summary>
    public bool? Checked { get; internal set; }
}

/// <summary>A Markdown document: its blocks, made by <see cref="MarkdownParser.Parse"/>.</summary>
public sealed class MarkdownDocument
{
    public List<MarkdownBlock> Blocks { get; } = new List<MarkdownBlock>();

    /// <summary>
    /// The index of the block of <see cref="Blocks"/> that shows a line of the source: the block that holds the line, or else the last one
    /// before it (a blank line between two blocks belongs to the first); -1 when there is no block before the line.
    /// </summary>
    public int BlockIndexAtLine(int line)
    {
        int found = -1;
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (Blocks[i].Line > line)
            {
                break;
            }
            found = i;
        }
        return found;
    }

    /// <summary>The heading whose <see cref="MarkdownBlock.Anchor"/> is <paramref name="anchor"/> (case ignored), at any depth; null when there is none.</summary>
    public MarkdownBlock FindHeading(string anchor)
    {
        if (string.IsNullOrEmpty(anchor))
        {
            return null;
        }
        return FindHeading(Blocks, anchor.ToLowerInvariant());
    }

    private static MarkdownBlock FindHeading(List<MarkdownBlock> blocks, string anchor)
    {
        foreach (MarkdownBlock block in blocks)
        {
            if (block.Kind == MarkdownBlockKind.Heading && block.Anchor == anchor)
            {
                return block;
            }
            MarkdownBlock found = FindHeading(block.Children, anchor);
            foreach (MarkdownListItem item in block.Items)
            {
                found = found ?? FindHeading(item.Blocks, anchor);
            }
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}
