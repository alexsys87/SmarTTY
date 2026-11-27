using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.TextEditing;

/// <summary>
/// Reads Markdown into a <see cref="MarkdownDocument"/> for the preview of the editor: the blocks of CommonMark (paragraphs, ATX and setext
/// headings, fenced and indented code, block quotes, bulleted and numbered lists, rules, HTML blocks, link reference definitions) and of
/// GitHub (tables, task items, struck text, addresses without angle brackets), and the inlines (emphasis by the delimiter rules of CommonMark,
/// code spans, links and images, inline and by reference, autolinks, backslash escapes, entities, hard line breaks). It is not a full
/// implementation of the specification: a lazy continuation line of a quote or a list item is taken whatever the block it continues, and
/// the HTML of a document is shown as its text (only <c>&lt;br&gt;</c> and <c>&lt;img&gt;</c> mean something). It never throws on any text.
/// </summary>
public static class MarkdownParser
{
    // A line of the source: the text with the tabs of its indentation turned into blanks, and its number in the document.
    private struct Line
    {
        public Line(string text, int number)
        {
            Text = text;
            Number = number;
        }

        public readonly string Text;

        public readonly int Number;
    }

    private struct ListMarker
    {
        public bool Ordered;

        // The bullet (- * +) or the delimiter after the number (. or )).
        public char Symbol;

        public int Number;

        // The column the content of the item starts at.
        public int ContentIndent;

        public bool EmptyContent;
    }

    private sealed class LinkDefinition
    {
        public string Url;

        public string Title;
    }

    // The deepest nesting of quotes and lists that is parsed as blocks (deeper ones are a paragraph): a guard against a stack overflow.
    private const int MaxDepth = 64;

    // The deepest nesting of emphasis and links (deeper delimiters stay text), for the same reason: the inlines are walked recursively.
    private const int MaxInlineDepth = 32;

    private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "base", "basefont", "blockquote", "body", "caption", "center", "col", "colgroup", "dd", "details",
        "dialog", "dir", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4",
        "h5", "h6", "head", "header", "hr", "html", "iframe", "legend", "li", "link", "main", "menu", "menuitem", "nav", "noframes", "ol",
        "optgroup", "option", "p", "param", "picture", "section", "source", "summary", "table", "tbody", "td", "tfoot", "th", "thead",
        "title", "tr", "track", "ul"
    };

    // The tags after which the text of an HTML block goes on in a new line.
    private static readonly HashSet<string> BreakingTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "br", "p", "div", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "summary", "table", "ul", "ol", "dt", "dd", "blockquote", "hr",
        "section", "header", "footer", "center"
    };

    private static readonly Regex OpenTagPattern = new Regex(
        @"\G<([A-Za-z][A-Za-z0-9-]*)((?:\s+[A-Za-z_:][A-Za-z0-9_.:-]*(?:\s*=\s*(?:[^\s""'=<>`]+|'[^']*'|""[^""]*""))?)*)\s*/?>",
        RegexOptions.CultureInvariant);

    private static readonly Regex CloseTagPattern = new Regex(@"\G</([A-Za-z][A-Za-z0-9-]*)\s*>", RegexOptions.CultureInvariant);

    private static readonly Regex AttributePattern = new Regex(
        @"([A-Za-z_:][A-Za-z0-9_.:-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+))", RegexOptions.CultureInvariant);

    private static readonly Regex AutolinkPattern = new Regex(@"\G<([A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*)>", RegexOptions.CultureInvariant);

    private static readonly Regex EmailPattern = new Regex(
        @"\G<([A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*)>",
        RegexOptions.CultureInvariant);

    private static readonly Regex EntityPattern = new Regex(@"\G&(?:#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});", RegexOptions.CultureInvariant);

    private static readonly Regex DefinitionPattern = new Regex(
        @"^ {0,3}\[((?:[^\]\\]|\\.){1,999})\]:[ \t]*(<[^<>\n]*>|\S+)(?:[ \t]+(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\((?:[^()\\]|\\.)*\)))?[ \t]*$",
        RegexOptions.CultureInvariant);

    /// <summary>Reads a Markdown text (any line endings).</summary>
    public static MarkdownDocument Parse(string text)
    {
        List<Line> lines = SplitLines(text ?? "");
        Dictionary<string, LinkDefinition> definitions = new Dictionary<string, LinkDefinition>();
        MarkdownDocument document = new MarkdownDocument();
        document.Blocks.AddRange(ParseBlocks(lines, definitions, 0));
        // The inlines come second: a link may use a definition that is further down.
        Dictionary<string, int> anchors = new Dictionary<string, int>();
        ParseInlines(document.Blocks, definitions, anchors);
        return document;
    }

    /// <summary>
    /// The anchor GitHub gives a heading: its text in lower case, without the marks but the hyphen and the underscore, the blanks as hyphens.
    /// </summary>
    public static string MakeAnchor(string headingText)
    {
        StringBuilder anchor = new StringBuilder();
        foreach (char ch in (headingText ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                anchor.Append(ch);
            }
            else if (ch == ' ')
            {
                anchor.Append('-');
            }
            else
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark)
                {
                    anchor.Append(ch);
                }
            }
        }
        return anchor.ToString();
    }

    private static List<Line> SplitLines(string text)
    {
        List<Line> lines = new List<Line>();
        int start = 0;
        int number = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n' && text[i] != '\r')
            {
                continue;
            }
            lines.Add(new Line(ExpandIndentTabs(text.Substring(start, i - start)), number++));
            if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }
            start = i + 1;
        }
        // The line break that ends the last line makes no line of its own.
        if (lines.Count > 1 && lines[lines.Count - 1].Text.Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    // The tabs of the indentation (and of the blanks after a list marker or a quote) go to the next column of four.
    private static string ExpandIndentTabs(string text)
    {
        if (text.IndexOf('\t') < 0)
        {
            return text;
        }
        StringBuilder result = new StringBuilder();
        int i = 0;
        for (; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\t')
            {
                result.Append(' ', 4 - (result.Length % 4));
            }
            else if (ch == ' ' || ch == '>' || ch == '-' || ch == '*' || ch == '+' || ch == '.' || ch == ')' || char.IsDigit(ch))
            {
                result.Append(ch);
            }
            else
            {
                break;
            }
        }
        result.Append(text, i, text.Length - i);
        return result.ToString();
    }

    private static bool IsBlank(string text)
    {
        foreach (char ch in text)
        {
            if (ch != ' ' && ch != '\t')
            {
                return false;
            }
        }
        return true;
    }

    private static int Indent(string text)
    {
        int count = 0;
        while (count < text.Length && text[count] == ' ')
        {
            count++;
        }
        return count;
    }

    // The text after at most `count` blanks of indentation.
    private static string RemoveIndent(string text, int count)
    {
        int i = 0;
        while (i < count && i < text.Length && text[i] == ' ')
        {
            i++;
        }
        return text.Substring(i);
    }

    // ---------------------------------------------------------------- blocks

    private static List<MarkdownBlock> ParseBlocks(List<Line> lines, Dictionary<string, LinkDefinition> definitions, int depth)
    {
        List<MarkdownBlock> blocks = new List<MarkdownBlock>();
        int i = 0;
        while (i < lines.Count)
        {
            string text = lines[i].Text;
            if (IsBlank(text))
            {
                i++;
                continue;
            }
            int indent = Indent(text);
            if (indent >= 4)
            {
                i = ParseIndentedCode(lines, i, blocks);
                continue;
            }
            string rest = text.Substring(indent);
            if (TryFenceStart(rest, out char fenceChar, out int fenceLength, out string info))
            {
                i = ParseFencedCode(lines, i, indent, fenceChar, fenceLength, info, blocks);
                continue;
            }
            if (TryAtxHeading(rest, out int level, out string headingText))
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, lines[i].Number, lines[i].Number) { Level = level, RawText = headingText });
                i++;
                continue;
            }
            if (IsThematicBreak(rest))
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, lines[i].Number, lines[i].Number));
                i++;
                continue;
            }
            if (rest[0] == '>' && depth < MaxDepth)
            {
                i = ParseQuote(lines, i, blocks, definitions, depth);
                continue;
            }
            if (depth < MaxDepth && TryListMarker(text, out ListMarker marker))
            {
                i = ParseList(lines, i, marker, blocks, definitions, depth);
                continue;
            }
            if (TryHtmlBlockStart(rest, true, out string endMarker))
            {
                i = ParseHtmlBlock(lines, i, endMarker, blocks);
                continue;
            }
            if (i + 1 < lines.Count && TryTable(lines, i, out List<MarkdownAlignment> alignments, out List<string> header))
            {
                i = ParseTable(lines, i, alignments, header, blocks);
                continue;
            }
            i = ParseParagraph(lines, i, blocks, definitions);
        }
        return blocks;
    }

    private static int ParseIndentedCode(List<Line> lines, int start, List<MarkdownBlock> blocks)
    {
        int i = start;
        int last = start;
        while (i < lines.Count && (IsBlank(lines[i].Text) || Indent(lines[i].Text) >= 4))
        {
            if (!IsBlank(lines[i].Text))
            {
                last = i;
            }
            i++;
        }
        StringBuilder text = new StringBuilder();
        for (int k = start; k <= last; k++)
        {
            if (k > start)
            {
                text.Append('\n');
            }
            text.Append(RemoveIndent(lines[k].Text, 4));
        }
        blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, lines[start].Number, lines[last].Number) { Text = text.ToString() });
        return last + 1;
    }

    private static bool TryFenceStart(string rest, out char fenceChar, out int fenceLength, out string info)
    {
        fenceChar = '\0';
        fenceLength = 0;
        info = "";
        if (rest.Length < 3 || (rest[0] != '`' && rest[0] != '~'))
        {
            return false;
        }
        char ch = rest[0];
        int count = 0;
        while (count < rest.Length && rest[count] == ch)
        {
            count++;
        }
        if (count < 3)
        {
            return false;
        }
        string tail = rest.Substring(count).Trim();
        // The info string of a backtick fence has no backtick (it would be a code span).
        if (ch == '`' && tail.IndexOf('`') >= 0)
        {
            return false;
        }
        fenceChar = ch;
        fenceLength = count;
        info = UnescapeText(tail);
        return true;
    }

    private static bool IsFenceEnd(string text, char fenceChar, int fenceLength)
    {
        int indent = Indent(text);
        if (indent >= 4)
        {
            return false;
        }
        int count = 0;
        int i = indent;
        while (i < text.Length && text[i] == fenceChar)
        {
            count++;
            i++;
        }
        return count >= fenceLength && IsBlank(text.Substring(i));
    }

    private static int ParseFencedCode(List<Line> lines, int start, int indent, char fenceChar, int fenceLength, string info, List<MarkdownBlock> blocks)
    {
        int i = start + 1;
        StringBuilder text = new StringBuilder();
        bool first = true;
        int endLine = lines[start].Number;
        while (i < lines.Count && !IsFenceEnd(lines[i].Text, fenceChar, fenceLength))
        {
            if (!first)
            {
                text.Append('\n');
            }
            first = false;
            text.Append(RemoveIndent(lines[i].Text, indent));
            endLine = lines[i].Number;
            i++;
        }
        if (i < lines.Count)
        {
            // The closing fence.
            endLine = lines[i].Number;
            i++;
        }
        blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, lines[start].Number, endLine) { Text = text.ToString(), Info = info });
        return i;
    }

    private static bool TryAtxHeading(string rest, out int level, out string text)
    {
        level = 0;
        text = null;
        while (level < rest.Length && rest[level] == '#')
        {
            level++;
        }
        if (level < 1 || level > 6 || (level < rest.Length && rest[level] != ' ' && rest[level] != '\t'))
        {
            return false;
        }
        string content = rest.Substring(level).Trim();
        // A closing sequence of # after a blank (or the whole content) is not part of the heading.
        int end = content.Length;
        while (end > 0 && content[end - 1] == '#')
        {
            end--;
        }
        if (end == 0)
        {
            content = "";
        }
        else if (end < content.Length && content[end - 1] == ' ')
        {
            content = content.Substring(0, end).TrimEnd();
        }
        text = content;
        return true;
    }

    private static bool IsThematicBreak(string rest)
    {
        if (rest.Length == 0 || (rest[0] != '-' && rest[0] != '*' && rest[0] != '_'))
        {
            return false;
        }
        char ch = rest[0];
        int count = 0;
        foreach (char c in rest)
        {
            if (c == ch)
            {
                count++;
            }
            else if (c != ' ' && c != '\t')
            {
                return false;
            }
        }
        return count >= 3;
    }

    // A line of = or - under a paragraph: its level (1 or 2), or 0.
    private static int SetextLevel(string text)
    {
        if (Indent(text) >= 4)
        {
            return 0;
        }
        string rest = text.Trim();
        if (rest.Length == 0 || (rest[0] != '=' && rest[0] != '-'))
        {
            return 0;
        }
        foreach (char c in rest)
        {
            if (c != rest[0])
            {
                return 0;
            }
        }
        return rest[0] == '=' ? 1 : 2;
    }

    private static bool TryListMarker(string text, out ListMarker marker)
    {
        marker = default;
        int indent = Indent(text);
        if (indent >= 4 || indent >= text.Length)
        {
            return false;
        }
        int pos = indent;
        char ch = text[pos];
        if (ch == '-' || ch == '*' || ch == '+')
        {
            marker.Symbol = ch;
            pos++;
        }
        else
        {
            int digits = 0;
            while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9' && digits < 10)
            {
                pos++;
                digits++;
            }
            if (digits == 0 || digits > 9 || pos >= text.Length || (text[pos] != '.' && text[pos] != ')'))
            {
                return false;
            }
            marker.Ordered = true;
            marker.Number = int.Parse(text.Substring(indent, digits), CultureInfo.InvariantCulture);
            marker.Symbol = text[pos];
            pos++;
        }
        if (pos < text.Length && text[pos] != ' ')
        {
            return false;
        }
        int spaces = 0;
        while (pos + spaces < text.Length && text[pos + spaces] == ' ')
        {
            spaces++;
        }
        if (pos + spaces >= text.Length)
        {
            marker.EmptyContent = true;
            marker.ContentIndent = pos + 1;
        }
        else
        {
            // Five blanks or more: the content is an indented code block after one blank.
            marker.ContentIndent = pos + ((spaces >= 5) ? 1 : spaces);
        }
        return true;
    }

    // Whether a line ends a paragraph above it (it starts another block).
    private static bool InterruptsParagraph(string text)
    {
        if (IsBlank(text))
        {
            return true;
        }
        int indent = Indent(text);
        if (indent >= 4)
        {
            return false;
        }
        string rest = text.Substring(indent);
        if (TryFenceStart(rest, out _, out _, out _) || TryAtxHeading(rest, out _, out _) || IsThematicBreak(rest) || rest[0] == '>')
        {
            return true;
        }
        if (TryListMarker(text, out ListMarker marker))
        {
            // An empty item, or a numbered one that does not start at 1, does not end a paragraph.
            return !marker.EmptyContent && (!marker.Ordered || marker.Number == 1);
        }
        return TryHtmlBlockStart(rest, false, out _);
    }

    private static int ParseQuote(List<Line> lines, int start, List<MarkdownBlock> blocks, Dictionary<string, LinkDefinition> definitions, int depth)
    {
        List<Line> inner = new List<Line>();
        int i = start;
        bool lastWasText = false;
        while (i < lines.Count)
        {
            string text = lines[i].Text;
            int indent = Indent(text);
            if (indent < 4 && indent < text.Length && text[indent] == '>')
            {
                string content = text.Substring(indent + 1);
                if (content.StartsWith(" ", StringComparison.Ordinal))
                {
                    content = content.Substring(1);
                }
                inner.Add(new Line(content, lines[i].Number));
                lastWasText = !IsBlank(content);
                i++;
                continue;
            }
            // A lazy continuation line of a paragraph of the quote.
            if (lastWasText && !InterruptsParagraph(text))
            {
                inner.Add(new Line(text.TrimStart(), lines[i].Number));
                i++;
                continue;
            }
            break;
        }
        MarkdownBlock quote = new MarkdownBlock(MarkdownBlockKind.Quote, lines[start].Number, lines[i - 1].Number);
        quote.Children.AddRange(ParseBlocks(inner, definitions, depth + 1));
        blocks.Add(quote);
        return i;
    }

    private static int ParseList(List<Line> lines, int start, ListMarker first, List<MarkdownBlock> blocks, Dictionary<string, LinkDefinition> definitions, int depth)
    {
        MarkdownBlock list = new MarkdownBlock(MarkdownBlockKind.List, lines[start].Number, lines[start].Number)
        {
            Ordered = first.Ordered,
            Start = first.Ordered ? first.Number : 1
        };
        int i = start;
        bool tight = true;
        while (i < lines.Count && TryListMarker(lines[i].Text, out ListMarker marker)
            && marker.Ordered == first.Ordered && marker.Symbol == first.Symbol
            && !IsThematicBreak(lines[i].Text.TrimStart()))
        {
            List<Line> itemLines = new List<Line>();
            string firstText = lines[i].Text;
            itemLines.Add(new Line(marker.EmptyContent ? "" : firstText.Substring(Math.Min(marker.ContentIndent, firstText.Length)), lines[i].Number));
            int j = i + 1;
            bool lastWasText = !marker.EmptyContent;
            while (j < lines.Count)
            {
                string text = lines[j].Text;
                if (IsBlank(text))
                {
                    // An item that starts with a blank line holds nothing when the next line is blank too.
                    if (marker.EmptyContent && itemLines.Count == 1)
                    {
                        break;
                    }
                    itemLines.Add(new Line("", lines[j].Number));
                    lastWasText = false;
                    j++;
                    continue;
                }
                if (Indent(text) >= marker.ContentIndent)
                {
                    itemLines.Add(new Line(text.Substring(marker.ContentIndent), lines[j].Number));
                    lastWasText = true;
                    j++;
                    continue;
                }
                if (lastWasText && !InterruptsParagraph(text) && !TryListMarker(text, out _))
                {
                    itemLines.Add(new Line(text.TrimStart(), lines[j].Number));
                    j++;
                    continue;
                }
                break;
            }
            // The blank lines at the end belong between the items (or after the list).
            int trailingBlanks = 0;
            while (itemLines.Count > 1 && IsBlank(itemLines[itemLines.Count - 1].Text))
            {
                itemLines.RemoveAt(itemLines.Count - 1);
                trailingBlanks++;
            }
            MarkdownListItem item = new MarkdownListItem(lines[i].Number);
            item.Blocks.AddRange(ParseBlocks(itemLines, definitions, depth + 1));
            // A blank line between two blocks of an item makes the list loose (those inside a nested list or a code block do not).
            for (int k = 1; k < item.Blocks.Count; k++)
            {
                if (item.Blocks[k].Line > item.Blocks[k - 1].EndLine + 1)
                {
                    tight = false;
                }
            }
            TakeTaskMark(item);
            list.Items.Add(item);
            list.EndLine = itemLines[itemLines.Count - 1].Number;
            i = j;
            if (trailingBlanks > 0 && i < lines.Count && TryListMarker(lines[i].Text, out ListMarker next)
                && next.Ordered == first.Ordered && next.Symbol == first.Symbol)
            {
                tight = false;
            }
        }
        list.Tight = tight;
        blocks.Add(list);
        return i;
    }

    // [ ] or [x] at the start of the first paragraph of an item makes it a task.
    private static void TakeTaskMark(MarkdownListItem item)
    {
        if (item.Blocks.Count == 0 || item.Blocks[0].Kind != MarkdownBlockKind.Paragraph)
        {
            return;
        }
        string text = item.Blocks[0].RawText;
        if (text.Length >= 3 && text[0] == '[' && text[2] == ']' && (text[1] == ' ' || text[1] == 'x' || text[1] == 'X')
            && (text.Length == 3 || text[3] == ' ' || text[3] == '\n'))
        {
            item.Checked = text[1] != ' ';
            item.Blocks[0].RawText = text.Substring(Math.Min(4, text.Length));
        }
    }

    private static bool TryHtmlBlockStart(string rest, bool canBeStandaloneTag, out string endMarker)
    {
        endMarker = null;
        if (rest.Length < 2 || rest[0] != '<')
        {
            return false;
        }
        if (rest.StartsWith("<!--", StringComparison.Ordinal))
        {
            endMarker = "-->";
            return true;
        }
        Match tag = Regex.Match(rest, @"^</?([A-Za-z][A-Za-z0-9-]*)(?=[\s/>]|$)");
        if (!tag.Success)
        {
            return false;
        }
        string name = tag.Groups[1].Value;
        if (!rest.StartsWith("</", StringComparison.Ordinal) && (name.Equals("pre", StringComparison.OrdinalIgnoreCase)
            || name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)
            || name.Equals("textarea", StringComparison.OrdinalIgnoreCase)))
        {
            endMarker = "</" + name.ToLowerInvariant() + ">";
            return true;
        }
        if (BlockTags.Contains(name))
        {
            return true;
        }
        if (!canBeStandaloneTag)
        {
            return false;
        }
        // Any other tag alone on its line.
        string trimmed = rest.TrimEnd();
        Match open = OpenTagPattern.Match(trimmed, 0);
        Match close = CloseTagPattern.Match(trimmed, 0);
        return (open.Success && open.Length == trimmed.Length) || (close.Success && close.Length == trimmed.Length);
    }

    private static int ParseHtmlBlock(List<Line> lines, int start, string endMarker, List<MarkdownBlock> blocks)
    {
        int i = start;
        StringBuilder text = new StringBuilder();
        while (i < lines.Count)
        {
            string line = lines[i].Text;
            if (endMarker == null && IsBlank(line))
            {
                break;
            }
            if (text.Length > 0)
            {
                text.Append('\n');
            }
            text.Append(line);
            i++;
            if (endMarker != null && line.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                break;
            }
        }
        string raw = text.ToString();
        int end = Math.Max(start, i - 1);
        if (endMarker == "</pre>")
        {
            // Preformatted text is a code block without its tags.
            string code = Regex.Replace(raw, @"</?[A-Za-z][^>]*>", "").Trim('\n');
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, lines[start].Number, lines[end].Number) { Text = WebUtility.HtmlDecode(code) });
        }
        else if (endMarker == "</script>" || endMarker == "</style>")
        {
            // Nothing that shows.
        }
        else
        {
            // A comment shows nothing either; the text of the rest of it is kept (the inlines take the tags out).
            string shown = Regex.Replace(raw, "<!--.*?-->", "", RegexOptions.Singleline);
            if (endMarker == "-->" && shown.IndexOf("<!--", StringComparison.Ordinal) >= 0)
            {
                shown = shown.Substring(0, shown.IndexOf("<!--", StringComparison.Ordinal));
            }
            if (!IsBlank(shown.Replace('\n', ' ')))
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Html, lines[start].Number, lines[end].Number) { RawText = shown });
            }
        }
        return i;
    }

    private static bool TryTable(List<Line> lines, int start, out List<MarkdownAlignment> alignments, out List<string> header)
    {
        alignments = null;
        header = null;
        string headerText = lines[start].Text;
        if (headerText.IndexOf('|') < 0 || !TryAlignmentRow(lines[start + 1].Text, out alignments))
        {
            return false;
        }
        header = SplitRow(headerText);
        return header.Count == alignments.Count;
    }

    private static bool TryAlignmentRow(string text, out List<MarkdownAlignment> alignments)
    {
        alignments = new List<MarkdownAlignment>();
        if (Indent(text) >= 4 || text.IndexOf('-') < 0)
        {
            return false;
        }
        string trimmed = text.Trim();
        bool hasPipe = trimmed.IndexOf('|') >= 0;
        foreach (string cell in SplitRow(trimmed))
        {
            string c = cell.Trim();
            bool left = c.StartsWith(":", StringComparison.Ordinal);
            bool right = c.EndsWith(":", StringComparison.Ordinal) && c.Length > 1;
            string dashes = c.Substring(left ? 1 : 0, c.Length - (left ? 1 : 0) - (right ? 1 : 0));
            if (dashes.Length == 0 || dashes.Trim('-').Length != 0)
            {
                return false;
            }
            alignments.Add(left && right ? MarkdownAlignment.Center : left ? MarkdownAlignment.Left : right ? MarkdownAlignment.Right : MarkdownAlignment.None);
        }
        // A row of one column needs a pipe (otherwise it is a setext underline).
        return alignments.Count > 0 && (hasPipe || alignments.Count > 1);
    }

    // The cells of a table row: split at the pipes that are not escaped, without the pipes at both ends.
    private static List<string> SplitRow(string text)
    {
        string trimmed = text.Trim();
        List<string> cells = new List<string>();
        StringBuilder cell = new StringBuilder();
        int start = trimmed.StartsWith("|", StringComparison.Ordinal) ? 1 : 0;
        bool endPipe = false;
        for (int i = start; i < trimmed.Length; i++)
        {
            char ch = trimmed[i];
            if (ch == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }
            if (ch == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                endPipe = i == trimmed.Length - 1;
                continue;
            }
            cell.Append(ch);
        }
        if (!endPipe || cell.Length > 0)
        {
            cells.Add(cell.ToString().Trim());
        }
        return cells;
    }

    private static int ParseTable(List<Line> lines, int start, List<MarkdownAlignment> alignments, List<string> header, List<MarkdownBlock> blocks)
    {
        MarkdownBlock table = new MarkdownBlock(MarkdownBlockKind.Table, lines[start].Number, lines[start + 1].Number)
        {
            RawHeader = header,
            RawRows = new List<List<string>>()
        };
        table.Alignments.AddRange(alignments);
        int i = start + 2;
        while (i < lines.Count)
        {
            string text = lines[i].Text;
            if (IsBlank(text))
            {
                break;
            }
            int indent = Indent(text);
            string rest = text.Substring(Math.Min(indent, 3));
            if (indent < 4 && (rest[0] == '>' || TryFenceStart(rest, out _, out _, out _) || TryAtxHeading(rest, out _, out _) || IsThematicBreak(rest)))
            {
                break;
            }
            List<string> cells = SplitRow(text);
            while (cells.Count < alignments.Count)
            {
                cells.Add("");
            }
            if (cells.Count > alignments.Count)
            {
                cells.RemoveRange(alignments.Count, cells.Count - alignments.Count);
            }
            table.RawRows.Add(cells);
            table.EndLine = lines[i].Number;
            i++;
        }
        blocks.Add(table);
        return i;
    }

    private static int ParseParagraph(List<Line> lines, int start, List<MarkdownBlock> blocks, Dictionary<string, LinkDefinition> definitions)
    {
        List<Line> paragraph = new List<Line> { lines[start] };
        int i = start + 1;
        int setext = 0;
        while (i < lines.Count)
        {
            string text = lines[i].Text;
            int level = SetextLevel(text);
            if (level != 0)
            {
                setext = level;
                i++;
                break;
            }
            if (InterruptsParagraph(text) || (i + 1 < lines.Count && TryTable(lines, i, out _, out _)))
            {
                break;
            }
            paragraph.Add(lines[i]);
            i++;
        }
        // The link reference definitions at the start of the paragraph are no text.
        int first = 0;
        while (first < paragraph.Count)
        {
            Match match = DefinitionPattern.Match(paragraph[first].Text);
            if (!match.Success)
            {
                break;
            }
            string label = NormalizeLabel(match.Groups[1].Value);
            if (label.Length > 0 && !definitions.ContainsKey(label))
            {
                string url = match.Groups[2].Value;
                if (url.StartsWith("<", StringComparison.Ordinal))
                {
                    url = url.Substring(1, url.Length - 2);
                }
                string title = match.Groups[3].Success ? match.Groups[3].Value.Substring(1, match.Groups[3].Value.Length - 2) : null;
                definitions[label] = new LinkDefinition { Url = UnescapeText(url), Title = (title == null) ? null : UnescapeText(title) };
            }
            first++;
        }
        if (first == paragraph.Count)
        {
            if (setext != 0)
            {
                // The underline of nothing is a paragraph of its own (or a rule for ---).
                string underline = lines[i - 1].Text;
                blocks.Add(setext == 2 && IsThematicBreak(underline.Trim())
                    ? new MarkdownBlock(MarkdownBlockKind.Rule, lines[i - 1].Number, lines[i - 1].Number)
                    : new MarkdownBlock(MarkdownBlockKind.Paragraph, lines[i - 1].Number, lines[i - 1].Number) { RawText = underline.Trim() });
            }
            return i;
        }
        StringBuilder raw = new StringBuilder();
        for (int k = first; k < paragraph.Count; k++)
        {
            if (k > first)
            {
                raw.Append('\n');
            }
            raw.Append(paragraph[k].Text.TrimStart());
        }
        string content = raw.ToString().TrimEnd();
        int endLine = (setext != 0) ? lines[i - 1].Number : paragraph[paragraph.Count - 1].Number;
        blocks.Add(setext != 0
            ? new MarkdownBlock(MarkdownBlockKind.Heading, paragraph[first].Number, endLine) { Level = setext, RawText = content }
            : new MarkdownBlock(MarkdownBlockKind.Paragraph, paragraph[first].Number, endLine) { RawText = content });
        return i;
    }

    private static string NormalizeLabel(string label)
    {
        return Regex.Replace(label.Trim(), @"\s+", " ").ToUpperInvariant();
    }

    // ---------------------------------------------------------------- inlines

    private static void ParseInlines(List<MarkdownBlock> blocks, Dictionary<string, LinkDefinition> definitions, Dictionary<string, int> anchors)
    {
        foreach (MarkdownBlock block in blocks)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Paragraph:
                    block.Inlines = new InlineParser(block.RawText, definitions).Parse();
                    break;
                case MarkdownBlockKind.Heading:
                {
                    block.Inlines = new InlineParser(block.RawText, definitions).Parse();
                    string anchor = MakeAnchor(MarkdownInline.PlainText(block.Inlines));
                    if (anchors.TryGetValue(anchor, out int count))
                    {
                        anchors[anchor] = count + 1;
                        anchor = anchor + "-" + count.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        anchors[anchor] = 1;
                    }
                    block.Anchor = anchor;
                    break;
                }
                case MarkdownBlockKind.Html:
                    block.Inlines = HtmlBlockInlines(new InlineParser(block.RawText, definitions).Parse());
                    break;
                case MarkdownBlockKind.Table:
                    foreach (string cell in block.RawHeader)
                    {
                        block.Header.Add(new InlineParser(cell, definitions).Parse());
                    }
                    foreach (List<string> row in block.RawRows)
                    {
                        List<List<MarkdownInline>> cells = new List<List<MarkdownInline>>();
                        foreach (string cell in row)
                        {
                            cells.Add(new InlineParser(cell, definitions).Parse());
                        }
                        block.Rows.Add(cells);
                    }
                    break;
                case MarkdownBlockKind.Quote:
                    ParseInlines(block.Children, definitions, anchors);
                    break;
                case MarkdownBlockKind.List:
                    foreach (MarkdownListItem item in block.Items)
                    {
                        ParseInlines(item.Blocks, definitions, anchors);
                    }
                    break;
            }
            block.RawText = null;
            block.RawHeader = null;
            block.RawRows = null;
        }
        // An HTML block of tags only (</details>) shows nothing.
        blocks.RemoveAll(block => block.Kind == MarkdownBlockKind.Html && block.Inlines.Count == 0);
    }

    // In an HTML block the line breaks of the source are blanks, and the tags that end a line (</p>, <br>, </li>, ...) break it.
    private static List<MarkdownInline> HtmlBlockInlines(List<MarkdownInline> inlines)
    {
        List<MarkdownInline> result = new List<MarkdownInline>();
        foreach (MarkdownInline inline in inlines)
        {
            bool breaks = inline.Kind == MarkdownInlineKind.LineBreak
                || (inline.Kind == MarkdownInlineKind.Html && BreakingTags.Contains(TagName(inline.Text)) && (inline.Text.StartsWith("</", StringComparison.Ordinal)
                    || TagName(inline.Text).Equals("br", StringComparison.OrdinalIgnoreCase) || TagName(inline.Text).Equals("hr", StringComparison.OrdinalIgnoreCase)));
            if (breaks)
            {
                if (result.Count > 0 && result[result.Count - 1].Kind != MarkdownInlineKind.LineBreak)
                {
                    result.Add(new MarkdownInline(MarkdownInlineKind.LineBreak));
                }
                continue;
            }
            if (inline.Kind == MarkdownInlineKind.SoftBreak)
            {
                if (result.Count > 0 && result[result.Count - 1].Kind != MarkdownInlineKind.LineBreak)
                {
                    result.Add(new MarkdownInline(MarkdownInlineKind.SoftBreak));
                }
                continue;
            }
            if (inline.Kind == MarkdownInlineKind.Html)
            {
                continue;
            }
            result.Add(inline);
        }
        while (result.Count > 0 && (result[result.Count - 1].Kind == MarkdownInlineKind.LineBreak || result[result.Count - 1].Kind == MarkdownInlineKind.SoftBreak))
        {
            result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    private static string TagName(string tag)
    {
        Match match = Regex.Match(tag ?? "", @"^</?([A-Za-z][A-Za-z0-9-]*)");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static bool IsAsciiPunctuation(char ch)
    {
        return (ch >= '!' && ch <= '/') || (ch >= ':' && ch <= '@') || (ch >= '[' && ch <= '`') || (ch >= '{' && ch <= '~');
    }

    private static bool IsPunctuation(char ch)
    {
        if (IsAsciiPunctuation(ch))
        {
            return true;
        }
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category == UnicodeCategory.ConnectorPunctuation || category == UnicodeCategory.DashPunctuation
            || category == UnicodeCategory.OpenPunctuation || category == UnicodeCategory.ClosePunctuation
            || category == UnicodeCategory.InitialQuotePunctuation || category == UnicodeCategory.FinalQuotePunctuation
            || category == UnicodeCategory.OtherPunctuation || category == UnicodeCategory.MathSymbol || category == UnicodeCategory.CurrencySymbol
            || category == UnicodeCategory.ModifierSymbol || category == UnicodeCategory.OtherSymbol;
    }

    // Backslash escapes and entities of a link target, a title or an info string.
    private static string UnescapeText(string text)
    {
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
            {
                result.Append(text[i + 1]);
                i++;
                continue;
            }
            if (ch == '&')
            {
                Match entity = EntityPattern.Match(text, i);
                if (entity.Success)
                {
                    result.Append(DecodeEntity(entity.Value));
                    i += entity.Length - 1;
                    continue;
                }
            }
            result.Append(ch);
        }
        return result.ToString();
    }

    private static string DecodeEntity(string entity)
    {
        string decoded = WebUtility.HtmlDecode(entity);
        // A character number of 0 or past Unicode is the replacement character.
        if (decoded == entity && entity.StartsWith("&#", StringComparison.Ordinal))
        {
            return "�";
        }
        return decoded.Length > 0 && decoded[0] == '\0' ? "�" : decoded;
    }

    private sealed class Delimiter
    {
        public LinkedListNode<MarkdownInline> Node;

        public char Char;

        public int Count;

        public int OriginalCount;

        public bool CanOpen;

        public bool CanClose;

        // The order in which the delimiters were found (the indexes of the list change while emphasis is made).
        public int Order;
    }

    private sealed class Bracket
    {
        public LinkedListNode<MarkdownInline> Node;

        public bool Image;

        public bool Active = true;

        // The number of delimiters before the bracket.
        public int DelimiterCount;

        // The position after [ or ![ in the text.
        public int TextStart;
    }

    private sealed class InlineParser
    {
        private readonly string _s;

        private readonly Dictionary<string, LinkDefinition> _definitions;

        private readonly LinkedList<MarkdownInline> _nodes = new LinkedList<MarkdownInline>();

        private readonly List<Delimiter> _delimiters = new List<Delimiter>();

        private readonly List<Bracket> _brackets = new List<Bracket>();

        private readonly StringBuilder _text = new StringBuilder();

        private int _pos;

        private int _order;

        public InlineParser(string text, Dictionary<string, LinkDefinition> definitions)
        {
            _s = text ?? "";
            _definitions = definitions;
        }

        public List<MarkdownInline> Parse()
        {
            while (_pos < _s.Length)
            {
                char ch = _s[_pos];
                switch (ch)
                {
                    case '\\':
                        ParseBackslash();
                        break;
                    case '`':
                        ParseCodeSpan();
                        break;
                    case '*':
                    case '_':
                    case '~':
                        ParseDelimiterRun(ch);
                        break;
                    case '[':
                        OpenBracket(false, 1);
                        break;
                    case '!':
                        if (_pos + 1 < _s.Length && _s[_pos + 1] == '[')
                        {
                            OpenBracket(true, 2);
                        }
                        else
                        {
                            _text.Append(ch);
                            _pos++;
                        }
                        break;
                    case ']':
                        CloseBracket();
                        break;
                    case '<':
                        ParseAngle();
                        break;
                    case '&':
                        ParseEntity();
                        break;
                    case '\n':
                        ParseNewline();
                        break;
                    default:
                        if (!TryBareUrl())
                        {
                            _text.Append(ch);
                            _pos++;
                        }
                        break;
                }
            }
            Flush();
            ProcessEmphasis(-1);
            List<MarkdownInline> result = new List<MarkdownInline>();
            foreach (MarkdownInline node in _nodes)
            {
                result.Add(node);
            }
            return Merge(result);
        }

        // Joins the neighbouring texts that the markers left apart, and drops the empty ones.
        private static List<MarkdownInline> Merge(List<MarkdownInline> nodes)
        {
            List<MarkdownInline> result = new List<MarkdownInline>();
            foreach (MarkdownInline node in nodes)
            {
                if (node.Kind == MarkdownInlineKind.Text)
                {
                    if (string.IsNullOrEmpty(node.Text))
                    {
                        continue;
                    }
                    MarkdownInline last = (result.Count > 0) ? result[result.Count - 1] : null;
                    if (last != null && last.Kind == MarkdownInlineKind.Text)
                    {
                        last.Text += node.Text;
                        continue;
                    }
                    node.IsMarker = false;
                }
                if (node.Children.Count > 0)
                {
                    List<MarkdownInline> children = Merge(node.Children);
                    node.Children.Clear();
                    node.Children.AddRange(children);
                }
                result.Add(node);
            }
            return result;
        }

        private void Flush()
        {
            if (_text.Length > 0)
            {
                _nodes.AddLast(new MarkdownInline(MarkdownInlineKind.Text, _text.ToString()));
                _text.Clear();
            }
        }

        private LinkedListNode<MarkdownInline> AddNode(MarkdownInline inline)
        {
            Flush();
            return _nodes.AddLast(inline);
        }

        private void ParseBackslash()
        {
            if (_pos + 1 < _s.Length && _s[_pos + 1] == '\n')
            {
                AddNode(new MarkdownInline(MarkdownInlineKind.LineBreak));
                _pos += 2;
                SkipLeadingBlanks();
                return;
            }
            if (_pos + 1 < _s.Length && IsAsciiPunctuation(_s[_pos + 1]))
            {
                _text.Append(_s[_pos + 1]);
                _pos += 2;
                return;
            }
            _text.Append('\\');
            _pos++;
        }

        private void SkipLeadingBlanks()
        {
            while (_pos < _s.Length && _s[_pos] == ' ')
            {
                _pos++;
            }
        }

        private void ParseNewline()
        {
            // Two blanks or more before the line break make it a hard one; the blanks are never shown.
            int blanks = 0;
            while (blanks < _text.Length && _text[_text.Length - 1 - blanks] == ' ')
            {
                blanks++;
            }
            _text.Length -= blanks;
            AddNode(new MarkdownInline(blanks >= 2 ? MarkdownInlineKind.LineBreak : MarkdownInlineKind.SoftBreak));
            _pos++;
            SkipLeadingBlanks();
        }

        private void ParseCodeSpan()
        {
            int start = _pos;
            int count = 0;
            while (_pos < _s.Length && _s[_pos] == '`')
            {
                count++;
                _pos++;
            }
            int search = _pos;
            while (search < _s.Length)
            {
                int close = _s.IndexOf('`', search);
                if (close < 0)
                {
                    break;
                }
                int closeCount = 0;
                while (close + closeCount < _s.Length && _s[close + closeCount] == '`')
                {
                    closeCount++;
                }
                if (closeCount == count)
                {
                    string code = _s.Substring(_pos, close - _pos).Replace('\n', ' ');
                    if (code.Length >= 2 && code[0] == ' ' && code[code.Length - 1] == ' ' && code.Trim(' ').Length > 0)
                    {
                        code = code.Substring(1, code.Length - 2);
                    }
                    AddNode(new MarkdownInline(MarkdownInlineKind.Code, code));
                    _pos = close + closeCount;
                    return;
                }
                search = close + closeCount;
            }
            // No closing run of the same length: the backticks are text.
            _text.Append(_s, start, count);
        }

        private void ParseDelimiterRun(char ch)
        {
            int start = _pos;
            while (_pos < _s.Length && _s[_pos] == ch)
            {
                _pos++;
            }
            int count = _pos - start;
            if (ch == '~' && count > 2)
            {
                _text.Append(ch, count);
                return;
            }
            char before = (start > 0) ? _s[start - 1] : ' ';
            char after = (_pos < _s.Length) ? _s[_pos] : ' ';
            bool beforeBlank = char.IsWhiteSpace(before);
            bool afterBlank = char.IsWhiteSpace(after);
            bool beforePunct = IsPunctuation(before);
            bool afterPunct = IsPunctuation(after);
            bool leftFlanking = !afterBlank && (!afterPunct || beforeBlank || beforePunct);
            bool rightFlanking = !beforeBlank && (!beforePunct || afterBlank || afterPunct);
            bool canOpen;
            bool canClose;
            if (ch == '_')
            {
                canOpen = leftFlanking && (!rightFlanking || beforePunct);
                canClose = rightFlanking && (!leftFlanking || afterPunct);
            }
            else
            {
                canOpen = leftFlanking;
                canClose = rightFlanking;
            }
            LinkedListNode<MarkdownInline> node = AddNode(new MarkdownInline(MarkdownInlineKind.Text, new string(ch, count)) { IsMarker = true });
            if (canOpen || canClose)
            {
                _delimiters.Add(new Delimiter
                {
                    Node = node,
                    Char = ch,
                    Count = count,
                    OriginalCount = count,
                    CanOpen = canOpen,
                    CanClose = canClose,
                    Order = _order++
                });
            }
        }

        private void OpenBracket(bool image, int length)
        {
            LinkedListNode<MarkdownInline> node = AddNode(new MarkdownInline(MarkdownInlineKind.Text, image ? "![" : "[") { IsMarker = true });
            _brackets.Add(new Bracket { Node = node, Image = image, DelimiterCount = _delimiters.Count, TextStart = _pos + length });
            _pos += length;
        }

        private void CloseBracket()
        {
            if (_brackets.Count == 0)
            {
                _text.Append(']');
                _pos++;
                return;
            }
            Bracket bracket = _brackets[_brackets.Count - 1];
            _brackets.RemoveAt(_brackets.Count - 1);
            if (!bracket.Active)
            {
                _text.Append(']');
                _pos++;
                return;
            }
            int closePos = _pos;
            int after = _pos + 1;
            string url = null;
            string title = null;
            int end = -1;
            if (after < _s.Length && _s[after] == '(' && TryLinkTail(after, out url, out title, out end))
            {
                // An inline link.
            }
            else
            {
                string label = null;
                end = after;
                if (after < _s.Length && _s[after] == '[')
                {
                    int labelEnd = FindLabelEnd(after + 1);
                    if (labelEnd > after + 1)
                    {
                        label = _s.Substring(after + 1, labelEnd - after - 1);
                        end = labelEnd + 1;
                    }
                    else if (labelEnd == after + 1)
                    {
                        // A collapsed reference: [text][].
                        end = labelEnd + 1;
                    }
                }
                label = label ?? _s.Substring(bracket.TextStart, closePos - bracket.TextStart);
                if (_definitions.TryGetValue(NormalizeLabel(label), out LinkDefinition definition))
                {
                    url = definition.Url;
                    title = definition.Title;
                }
                else
                {
                    url = null;
                }
            }
            if (url == null)
            {
                _text.Append(']');
                _pos++;
                return;
            }
            Flush();
            ProcessEmphasis(bracket.DelimiterCount - 1);
            if (DepthBetween(bracket.Node, null) >= MaxInlineDepth)
            {
                _text.Append(']');
                _pos++;
                return;
            }
            MarkdownInline link = new MarkdownInline(bracket.Image ? MarkdownInlineKind.Image : MarkdownInlineKind.Link)
            {
                Url = url,
                Title = title
            };
            link.Depth = DepthBetween(bracket.Node, null) + 1;
            LinkedListNode<MarkdownInline> current = bracket.Node.Next;
            while (current != null)
            {
                LinkedListNode<MarkdownInline> next = current.Next;
                _nodes.Remove(current);
                link.Children.Add(current.Value);
                current = next;
            }
            if (bracket.Image)
            {
                link.Text = MarkdownInline.PlainText(link.Children);
                link.Children.Clear();
            }
            else
            {
                // No link inside a link: the brackets before this one stay text.
                foreach (Bracket earlier in _brackets)
                {
                    if (!earlier.Image)
                    {
                        earlier.Active = false;
                    }
                }
            }
            _nodes.AddAfter(bracket.Node, link);
            _nodes.Remove(bracket.Node);
            _pos = end;
        }

        // The deepest nesting of the inlines between two nodes.
        private static int DepthBetween(LinkedListNode<MarkdownInline> first, LinkedListNode<MarkdownInline> last)
        {
            int depth = 0;
            for (LinkedListNode<MarkdownInline> node = first.Next; node != null && node != last; node = node.Next)
            {
                depth = Math.Max(depth, node.Value.Depth);
            }
            return depth;
        }

        // The ] that ends a link label starting at `start` (after its [), or -1.
        private int FindLabelEnd(int start)
        {
            for (int i = start; i < _s.Length && i - start < 1000; i++)
            {
                char ch = _s[i];
                if (ch == '\\')
                {
                    i++;
                    continue;
                }
                if (ch == '[')
                {
                    return -1;
                }
                if (ch == ']')
                {
                    return i;
                }
            }
            return -1;
        }

        // (destination "title") after the ] of a link.
        private bool TryLinkTail(int open, out string url, out string title, out int end)
        {
            url = null;
            title = null;
            end = -1;
            int i = open + 1;
            i = SkipSpace(i);
            if (i >= _s.Length)
            {
                return false;
            }
            StringBuilder destination = new StringBuilder();
            if (_s[i] == '<')
            {
                i++;
                while (i < _s.Length && _s[i] != '>')
                {
                    if (_s[i] == '\n' || _s[i] == '<')
                    {
                        return false;
                    }
                    if (_s[i] == '\\' && i + 1 < _s.Length && IsAsciiPunctuation(_s[i + 1]))
                    {
                        i++;
                    }
                    destination.Append(_s[i]);
                    i++;
                }
                if (i >= _s.Length)
                {
                    return false;
                }
                i++;
            }
            else
            {
                int depth = 0;
                while (i < _s.Length && !char.IsWhiteSpace(_s[i]) && !char.IsControl(_s[i]))
                {
                    char ch = _s[i];
                    if (ch == '\\' && i + 1 < _s.Length && IsAsciiPunctuation(_s[i + 1]))
                    {
                        destination.Append(_s[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (ch == '(')
                    {
                        depth++;
                    }
                    else if (ch == ')')
                    {
                        if (depth == 0)
                        {
                            break;
                        }
                        depth--;
                    }
                    destination.Append(ch);
                    i++;
                }
                if (depth != 0)
                {
                    return false;
                }
            }
            int beforeSpace = i;
            i = SkipSpace(i);
            if (i < _s.Length && i > beforeSpace && (_s[i] == '"' || _s[i] == '\'' || _s[i] == '('))
            {
                char close = (_s[i] == '(') ? ')' : _s[i];
                StringBuilder titleText = new StringBuilder();
                int k = i + 1;
                while (k < _s.Length && _s[k] != close)
                {
                    if (_s[k] == '\\' && k + 1 < _s.Length && IsAsciiPunctuation(_s[k + 1]))
                    {
                        k++;
                    }
                    titleText.Append(_s[k]);
                    k++;
                }
                if (k >= _s.Length)
                {
                    return false;
                }
                title = UnescapeText(titleText.ToString());
                i = SkipSpace(k + 1);
            }
            if (i >= _s.Length || _s[i] != ')')
            {
                return false;
            }
            url = UnescapeText(destination.ToString());
            end = i + 1;
            return true;
        }

        private int SkipSpace(int i)
        {
            while (i < _s.Length && (_s[i] == ' ' || _s[i] == '\t' || _s[i] == '\n'))
            {
                i++;
            }
            return i;
        }

        private void ParseAngle()
        {
            Match autolink = AutolinkPattern.Match(_s, _pos);
            if (autolink.Success)
            {
                string target = autolink.Groups[1].Value;
                MarkdownInline link = new MarkdownInline(MarkdownInlineKind.Link) { Url = target };
                link.Children.Add(new MarkdownInline(MarkdownInlineKind.Text, target));
                AddNode(link);
                _pos += autolink.Length;
                return;
            }
            Match email = EmailPattern.Match(_s, _pos);
            if (email.Success)
            {
                string address = email.Groups[1].Value;
                MarkdownInline link = new MarkdownInline(MarkdownInlineKind.Link) { Url = "mailto:" + address };
                link.Children.Add(new MarkdownInline(MarkdownInlineKind.Text, address));
                AddNode(link);
                _pos += email.Length;
                return;
            }
            if (_s.Length > _pos + 3 && string.CompareOrdinal(_s, _pos, "<!--", 0, 4) == 0)
            {
                int close = _s.IndexOf("-->", _pos + 4, StringComparison.Ordinal);
                if (close >= 0)
                {
                    AddNode(new MarkdownInline(MarkdownInlineKind.Html, _s.Substring(_pos, close + 3 - _pos)));
                    _pos = close + 3;
                    return;
                }
            }
            Match open = OpenTagPattern.Match(_s, _pos);
            if (open.Success)
            {
                string name = open.Groups[1].Value;
                if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
                {
                    AddNode(new MarkdownInline(MarkdownInlineKind.LineBreak));
                }
                else if (name.Equals("img", StringComparison.OrdinalIgnoreCase))
                {
                    AddNode(new MarkdownInline(MarkdownInlineKind.Image, Attribute(open.Groups[2].Value, "alt") ?? "")
                    {
                        Url = Attribute(open.Groups[2].Value, "src") ?? "",
                        Title = Attribute(open.Groups[2].Value, "title"),
                        Width = MarkdownImages.ParseLength(Attribute(open.Groups[2].Value, "width")),
                        Height = MarkdownImages.ParseLength(Attribute(open.Groups[2].Value, "height"))
                    });
                }
                else
                {
                    AddNode(new MarkdownInline(MarkdownInlineKind.Html, open.Value));
                }
                _pos += open.Length;
                return;
            }
            Match close2 = CloseTagPattern.Match(_s, _pos);
            if (close2.Success)
            {
                AddNode(new MarkdownInline(MarkdownInlineKind.Html, close2.Value));
                _pos += close2.Length;
                return;
            }
            _text.Append('<');
            _pos++;
        }

        private static string Attribute(string attributes, string name)
        {
            foreach (Match match in AttributePattern.Matches(attributes))
            {
                if (match.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    string value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                    return WebUtility.HtmlDecode(value);
                }
            }
            return null;
        }

        private void ParseEntity()
        {
            Match entity = EntityPattern.Match(_s, _pos);
            if (entity.Success)
            {
                string decoded = DecodeEntity(entity.Value);
                if (decoded != entity.Value)
                {
                    _text.Append(decoded);
                    _pos += entity.Length;
                    return;
                }
            }
            _text.Append('&');
            _pos++;
        }

        // An address without angle brackets (GitHub): http://, https:// or www. at the start of a word.
        private bool TryBareUrl()
        {
            char ch = _s[_pos];
            if (ch != 'h' && ch != 'H' && ch != 'w' && ch != 'W')
            {
                return false;
            }
            if (_pos > 0)
            {
                char before = _s[_pos - 1];
                if (!char.IsWhiteSpace(before) && before != '*' && before != '_' && before != '~' && before != '(')
                {
                    return false;
                }
            }
            int prefix;
            bool www = false;
            if (StartsWithIgnoreCase("https://"))
            {
                prefix = 8;
            }
            else if (StartsWithIgnoreCase("http://"))
            {
                prefix = 7;
            }
            else if (StartsWithIgnoreCase("www."))
            {
                prefix = 4;
                www = true;
            }
            else
            {
                return false;
            }
            int end = _pos + prefix;
            while (end < _s.Length && !char.IsWhiteSpace(_s[end]) && _s[end] != '<')
            {
                end++;
            }
            // The marks at the end of a sentence are not part of the address, nor a ) that closes no ( of it.
            while (end > _pos + prefix)
            {
                char last = _s[end - 1];
                if ("?!.,:*_~'\"".IndexOf(last) >= 0)
                {
                    end--;
                    continue;
                }
                if (last == ')')
                {
                    string candidate = _s.Substring(_pos, end - _pos);
                    if (Count(candidate, ')') > Count(candidate, '('))
                    {
                        end--;
                        continue;
                    }
                }
                break;
            }
            string address = _s.Substring(_pos, end - _pos);
            string host = address.Substring(prefix);
            if (host.Length == 0 || (www && host.IndexOf('.') <= 0))
            {
                return false;
            }
            MarkdownInline link = new MarkdownInline(MarkdownInlineKind.Link) { Url = www ? "http://" + address : address };
            link.Children.Add(new MarkdownInline(MarkdownInlineKind.Text, address));
            AddNode(link);
            _pos = end;
            return true;
        }

        private static int Count(string text, char ch)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c == ch)
                {
                    count++;
                }
            }
            return count;
        }

        private bool StartsWithIgnoreCase(string prefix)
        {
            return _s.Length - _pos >= prefix.Length && string.Compare(_s, _pos, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        // The emphasis of CommonMark ("process emphasis"), for the delimiters above `bottom` (an index of the list).
        private void ProcessEmphasis(int bottom)
        {
            // For each kind of closer, the order of the delimiter at or below which no opener was found for it.
            Dictionary<int, int> openersBottom = new Dictionary<int, int>();
            int closerIndex = bottom + 1;
            while (closerIndex < _delimiters.Count)
            {
                Delimiter closer = _delimiters[closerIndex];
                if (!closer.CanClose)
                {
                    closerIndex++;
                    continue;
                }
                int key = closer.Char * 8 + (closer.CanOpen ? 4 : 0) + (closer.OriginalCount % 3);
                int lowest = openersBottom.TryGetValue(key, out int order) ? order : int.MinValue;
                int openerIndex = -1;
                for (int k = closerIndex - 1; k > bottom && _delimiters[k].Order > lowest; k--)
                {
                    Delimiter opener = _delimiters[k];
                    if (opener.Char != closer.Char || !opener.CanOpen)
                    {
                        continue;
                    }
                    if (closer.Char == '~')
                    {
                        if (opener.Count == closer.Count)
                        {
                            openerIndex = k;
                            break;
                        }
                        continue;
                    }
                    bool oddMatch = (opener.CanClose || closer.CanOpen) && (opener.OriginalCount + closer.OriginalCount) % 3 == 0
                        && !(opener.OriginalCount % 3 == 0 && closer.OriginalCount % 3 == 0);
                    if (!oddMatch)
                    {
                        openerIndex = k;
                        break;
                    }
                }
                if (openerIndex < 0)
                {
                    openersBottom[key] = (closerIndex > 0) ? _delimiters[closerIndex - 1].Order : int.MinValue;
                    if (!closer.CanOpen)
                    {
                        _delimiters.RemoveAt(closerIndex);
                    }
                    else
                    {
                        closerIndex++;
                    }
                    continue;
                }
                Delimiter open = _delimiters[openerIndex];
                if (DepthBetween(open.Node, closer.Node) >= MaxInlineDepth)
                {
                    // Too deep: this closer closes nothing.
                    if (!closer.CanOpen)
                    {
                        _delimiters.RemoveAt(closerIndex);
                    }
                    else
                    {
                        closer.CanClose = false;
                        closerIndex++;
                    }
                    continue;
                }
                int use = (closer.Char == '~') ? closer.Count : (open.Count >= 2 && closer.Count >= 2) ? 2 : 1;
                MarkdownInlineKind kind = (closer.Char == '~') ? MarkdownInlineKind.Strikethrough : (use == 2) ? MarkdownInlineKind.Strong : MarkdownInlineKind.Emphasis;
                open.Count -= use;
                closer.Count -= use;
                open.Node.Value.Text = new string(open.Char, open.Count);
                closer.Node.Value.Text = new string(closer.Char, closer.Count);
                MarkdownInline emphasis = new MarkdownInline(kind) { Depth = DepthBetween(open.Node, closer.Node) + 1 };
                LinkedListNode<MarkdownInline> current = open.Node.Next;
                while (current != null && current != closer.Node)
                {
                    LinkedListNode<MarkdownInline> next = current.Next;
                    _nodes.Remove(current);
                    emphasis.Children.Add(current.Value);
                    current = next;
                }
                _nodes.AddAfter(open.Node, emphasis);
                _delimiters.RemoveRange(openerIndex + 1, closerIndex - openerIndex - 1);
                closerIndex = openerIndex + 1;
                if (open.Count == 0)
                {
                    _nodes.Remove(open.Node);
                    _delimiters.RemoveAt(openerIndex);
                    closerIndex--;
                }
                if (closer.Count == 0)
                {
                    _nodes.Remove(closer.Node);
                    _delimiters.RemoveAt(closerIndex);
                }
            }
            if (bottom + 1 < _delimiters.Count)
            {
                _delimiters.RemoveRange(bottom + 1, _delimiters.Count - bottom - 1);
            }
        }
    }
}
