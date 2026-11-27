using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of the model of the Markdown preview of the editor: the blocks and the inlines that TextEditing/MarkdownParser.cs reads (written as a
// short text by Dump), the lines of the source of each block, the anchors of the headings, the targets of the links (MarkdownLinks) and the
// colours of the code blocks (CodeEditing/MarkdownCodeColoring.cs); also that no text makes the parser throw or hang.
// Run with: dotnet run --project tests/MarkdownPreviewTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    // The document as a short text: p(...), h2#anchor(...), code[info](...), quote[...], ul/ol<start>+ (tight) or - (loose)[li(...) ...],
    // table[alignments](cells)(cells), hr, html(...); the blocks apart by "; ". Inlines: <em>, <b>, <s>, <a url>, <img url|alt>, {code}, \n
    // for a hard break, ' ' for a soft one, <html> for a tag that shows nothing.
    private static string Dump(string markdown)
    {
        return Blocks(MarkdownParser.Parse(markdown).Blocks);
    }

    private static string Blocks(List<MarkdownBlock> blocks)
    {
        List<string> parts = new List<string>();
        foreach (MarkdownBlock block in blocks)
        {
            parts.Add(Block(block));
        }
        return string.Join("; ", parts);
    }

    private static string Block(MarkdownBlock block)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Paragraph:
                return "p(" + Inlines(block.Inlines) + ")";
            case MarkdownBlockKind.Heading:
                return "h" + block.Level + "#" + block.Anchor + "(" + Inlines(block.Inlines) + ")";
            case MarkdownBlockKind.Code:
                return "code[" + block.Info + "](" + block.Text.Replace("\n", "\\n") + ")";
            case MarkdownBlockKind.Quote:
                return "quote[" + Blocks(block.Children) + "]";
            case MarkdownBlockKind.Rule:
                return "hr";
            case MarkdownBlockKind.Html:
                return "html(" + Inlines(block.Inlines) + ")";
            case MarkdownBlockKind.List:
            {
                StringBuilder text = new StringBuilder(block.Ordered ? "ol" + block.Start : "ul");
                text.Append(block.Tight ? "+[" : "-[");
                List<string> items = new List<string>();
                foreach (MarkdownListItem item in block.Items)
                {
                    string mark = item.Checked == null ? "" : item.Checked.Value ? "x" : "_";
                    items.Add("li" + mark + "(" + Blocks(item.Blocks) + ")");
                }
                return text.Append(string.Join(" ", items)).Append(']').ToString();
            }
            case MarkdownBlockKind.Table:
            {
                StringBuilder text = new StringBuilder("table[");
                foreach (MarkdownAlignment alignment in block.Alignments)
                {
                    text.Append(alignment.ToString()[0]);
                }
                text.Append(']');
                text.Append(Row(block.Header));
                foreach (List<List<MarkdownInline>> row in block.Rows)
                {
                    text.Append(Row(row));
                }
                return text.ToString();
            }
            default:
                return "?";
        }
    }

    private static string Row(List<List<MarkdownInline>> cells)
    {
        List<string> parts = new List<string>();
        foreach (List<MarkdownInline> cell in cells)
        {
            parts.Add(Inlines(cell));
        }
        return "(" + string.Join("|", parts) + ")";
    }

    private static string Inlines(List<MarkdownInline> inlines)
    {
        StringBuilder text = new StringBuilder();
        foreach (MarkdownInline inline in inlines)
        {
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Text:
                    text.Append(inline.Text);
                    break;
                case MarkdownInlineKind.Code:
                    text.Append('{').Append(inline.Text).Append('}');
                    break;
                case MarkdownInlineKind.Emphasis:
                    text.Append("<em>").Append(Inlines(inline.Children)).Append("</em>");
                    break;
                case MarkdownInlineKind.Strong:
                    text.Append("<b>").Append(Inlines(inline.Children)).Append("</b>");
                    break;
                case MarkdownInlineKind.Strikethrough:
                    text.Append("<s>").Append(Inlines(inline.Children)).Append("</s>");
                    break;
                case MarkdownInlineKind.Link:
                    text.Append("<a ").Append(inline.Url).Append(inline.Title == null ? "" : " \"" + inline.Title + "\"").Append('>')
                        .Append(Inlines(inline.Children)).Append("</a>");
                    break;
                case MarkdownInlineKind.Image:
                    text.Append("<img ").Append(inline.Url).Append('|').Append(inline.Text).Append('>');
                    break;
                case MarkdownInlineKind.SoftBreak:
                    text.Append(' ');
                    break;
                case MarkdownInlineKind.LineBreak:
                    text.Append("\\n");
                    break;
                case MarkdownInlineKind.Html:
                    text.Append("<html>");
                    break;
            }
        }
        return text.ToString();
    }

    private static void Case(string markdown, string expected, string name)
    {
        Equal(Dump(markdown), expected, name);
    }

    private static int Main()
    {
        try
        {
            Blocks();
            InlineText();
            Lists();
            Tables();
            Html();
            SourceLines();
            Links();
            CodeColors();
            Robustness();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message.StartsWith("FAIL:", StringComparison.Ordinal) ? exception.Message : "FAIL: " + exception);
            return 1;
        }
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }

    private static void Blocks()
    {
        Case("", "", "empty text: no blocks");
        Case("\n\n  \n", "", "blank lines: no blocks");
        Case("hello\nworld", "p(hello world)", "lines of a paragraph are joined by a soft break");
        Case("one\n\ntwo", "p(one); p(two)", "a blank line ends a paragraph");
        Case("# Title", "h1#title(Title)", "ATX heading");
        Case("###### six", "h6#six(six)", "level six");
        Case("####### seven", "p(####### seven)", "seven # are a paragraph");
        Case("#no", "p(#no)", "a # without a blank is text");
        Case("## Closed ##", "h2#closed(Closed)", "the closing # are dropped");
        Case("# C#", "h1#c(C#)", "a # in the text stays (anchor without it)");
        Case("#", "h1#()", "an empty heading");
        Case("Title\n=====", "h1#title(Title)", "setext heading 1");
        Case("Sub\n---", "h2#sub(Sub)", "setext heading 2");
        Case("a\nb\n===", "h1#a-b(a b)", "a setext heading of two lines");
        Case("---", "hr", "a rule");
        Case("* * *", "hr", "a rule of stars with blanks");
        Case("___", "hr", "a rule of underscores");
        Case("text\n***", "p(text); hr", "a rule of stars after a paragraph");
        Case("--", "p(--)", "two dashes are text");
        Case("```\ncode\n  indented\n```", "code[](code\\n  indented)", "fenced code");
        Case("```bash\nls -l\n```", "code[bash](ls -l)", "the info string");
        Case("~~~ python extra\nx\n~~~", "code[python extra](x)", "a tilde fence and its info string");
        Case("````\n```\n````", "code[](```)", "a shorter fence inside a longer one");
        Case("```\nnot closed", "code[](not closed)", "a fence not closed runs to the end");
        Case("  ```\n  a\n b\n  ```", "code[](a\\nb)", "the indentation of the fence is taken from the lines");
        Case("    code\n    more\n\n    after blank", "code[](code\\nmore\\n\\nafter blank)", "indented code with a blank line");
        Case("\tcode", "code[](code)", "a tab indents code");
        Case("para\n    not code", "p(para not code)", "indented text continues a paragraph");
        Case("> quoted\n> more", "quote[p(quoted more)]", "a block quote");
        Case("> quoted\nlazy", "quote[p(quoted lazy)]", "a lazy continuation line of a quote");
        Case("> # Head\n> text", "quote[h1#head(Head); p(text)]", "blocks inside a quote");
        Case("> outer\n>> inner", "quote[p(outer); quote[p(inner)]]", "nested quotes");
        Case("> a\n\nb", "quote[p(a)]; p(b)", "a blank line ends a quote");
        Case("[ref]: https://example.com\n\nSee [ref].", "p(See <a https://example.com>ref</a>.)", "a reference definition makes no paragraph");
        Case("para\n# Head", "p(para); h1#head(Head)", "a heading ends a paragraph");
        Case("para\n```\ncode\n```", "p(para); code[](code)", "a fence ends a paragraph");
        Case("para\n> quote", "p(para); quote[p(quote)]", "a quote ends a paragraph");
        Case("# Same\n# Same\n# Same", "h1#same(Same); h1#same-1(Same); h1#same-2(Same)", "repeated headings get numbered anchors");
        Case("# Hello, World! (2024)", "h1#hello-world-2024(Hello, World! (2024))", "the anchor drops the marks");
        Case("# Привет мир", "h1#привет-мир(Привет мир)", "the anchor keeps letters that are not ASCII");
        Equal(MarkdownParser.MakeAnchor("  Mixed_Case-Text  "), "mixed_case-text", "the anchor keeps - and _ and lowers the case");
    }

    private static void InlineText()
    {
        Case("*em* and _em_", "p(<em>em</em> and <em>em</em>)", "emphasis");
        Case("**strong** and __strong__", "p(<b>strong</b> and <b>strong</b>)", "strong");
        Case("***both***", "p(<em><b>both</b></em>)", "strong emphasis");
        Case("**bold *and em* bold**", "p(<b>bold <em>and em</em> bold</b>)", "emphasis inside strong");
        Case("~~gone~~", "p(<s>gone</s>)", "struck text");
        Case("~one~", "p(<s>one</s>)", "struck text with one tilde");
        Case("a ~~~three~~~", "p(a ~~~three~~~)", "three tildes are text");
        Case("snake_case_name", "p(snake_case_name)", "an underscore inside a word is text");
        Case("2 * 3 * 4", "p(2 * 3 * 4)", "stars between blanks are text");
        Case("*not closed", "p(*not closed)", "an emphasis not closed is text");
        Case("**unbalanced*", "p(*<em>unbalanced</em>)", "the rest of a longer opener stays text");
        Case("*foo**bar**baz*", "p(<em>foo<b>bar</b>baz</em>)", "the rule of three");
        Case("`code`", "p({code})", "a code span");
        Case("``a ` b``", "p({a ` b})", "a code span with a backtick");
        Case("` `` `", "p({``})", "one blank on each side is dropped");
        Case("`*not em*`", "p({*not em*})", "no emphasis in code");
        Case("`unclosed", "p(`unclosed)", "a backtick not closed is text");
        Case("\\*literal\\*", "p(*literal*)", "backslash escapes");
        Case("\\a", "p(\\a)", "a backslash before a letter stays");
        Case("a  \nb", "p(a\\nb)", "two blanks make a hard break");
        Case("a\\\nb", "p(a\\nb)", "a backslash makes a hard break");
        Case("a \nb", "p(a b)", "one blank is a soft break");
        Case("&amp; &lt; &copy; &#65; &#x42; &bogus;", "p(& < © A B &bogus;)", "entities");
        Case("[link](https://example.com)", "p(<a https://example.com>link</a>)", "an inline link");
        Case("[link](https://example.com \"Title\")", "p(<a https://example.com \"Title\">link</a>)", "a link with a title");
        Case("[link](<a b.md>)", "p(<a a b.md>link</a>)", "a destination in angle brackets");
        Case("[link](a(b)c)", "p(<a a(b)c>link</a>)", "balanced parentheses in a destination");
        Case("[link]()", "p(<a >link</a>)", "an empty destination");
        Case("[*em* link](u)", "p(<a u><em>em</em> link</a>)", "emphasis in a link");
        Case("*[link](u)*", "p(<em><a u>link</a></em>)", "a link in emphasis");
        Case("[a [b](u) c](v)", "p([a <a u>b</a> c](v))", "no link inside a link");
        Case("[not a link]", "p([not a link])", "brackets without a target");
        Case("[text] (u)", "p([text] (u))", "a blank between ] and ( is no link");
        Case("![alt *text*](img.png)", "p(<img img.png|alt text>)", "an image");
        Case("[![badge](b.svg)](https://ci)", "p(<a https://ci><img b.svg|badge></a>)", "an image in a link");
        Case("[x][r]\n\n[r]: /path \"T\"", "p(<a /path \"T\">x</a>)", "a full reference");
        Case("[r][]\n\n[R]: /p", "p(<a /p>r</a>)", "a collapsed reference, the case ignored");
        Case("<https://example.com/a?b>", "p(<a https://example.com/a?b>https://example.com/a?b</a>)", "an autolink");
        Case("<me@example.com>", "p(<a mailto:me@example.com>me@example.com</a>)", "an e-mail autolink");
        Case("see https://example.com/x.", "p(see <a https://example.com/x>https://example.com/x</a>.)", "a bare address without its full stop");
        Case("(www.example.com)", "p((<a http://www.example.com>www.example.com</a>))", "www. in parentheses");
        Case("https://en.wikipedia.org/wiki/A_(b)", "p(<a https://en.wikipedia.org/wiki/A_(b)>https://en.wikipedia.org/wiki/A_(b)</a>)", "a ) that closes a ( of the address");
        Case("xhttps://no", "p(xhttps://no)", "an address inside a word is text");
        Case("a <b>bold</b> c", "p(a <html>bold<html> c)", "inline HTML tags show nothing");
        Case("a<br>b", "p(a\\nb)", "<br> is a line break");
        Case("x <img src=\"a.png\" alt=\"Logo\"> y", "p(x <img a.png|Logo> y)", "an <img> tag is an image");
        Case("a <!-- note --> b", "p(a <html> b)", "an inline comment shows nothing");
        Case("a < b > c", "p(a < b > c)", "angle brackets that are no tag are text");
    }

    private static void Lists()
    {
        Case("- a\n- b", "ul+[li(p(a)) li(p(b))]", "a bulleted list");
        Case("* a\n+ b", "ul+[li(p(a))]; ul+[li(p(b))]", "another bullet starts another list");
        Case("1. a\n2. b", "ol1+[li(p(a)) li(p(b))]", "a numbered list");
        Case("3) a\n4) b", "ol3+[li(p(a)) li(p(b))]", "the start number and the ) delimiter");
        Case("- a\n\n- b", "ul-[li(p(a)) li(p(b))]", "a blank line between items makes the list loose");
        Case("- a\n\n  more", "ul-[li(p(a); p(more))]", "a second paragraph of an item");
        Case("- a\n  - b\n  - c\n- d", "ul+[li(p(a); ul+[li(p(b)) li(p(c))]) li(p(d))]", "a nested list");
        Case("1. one\n   - sub", "ol1+[li(p(one); ul+[li(p(sub))])]", "a bulleted list in a numbered one");
        Case("- a\nlazy", "ul+[li(p(a lazy))]", "a lazy continuation line of an item");
        Case("- [ ] todo\n- [x] done\n- [X] also", "ul+[li_(p(todo)) lix(p(done)) lix(p(also))]", "task items");
        Case("- [link](u)", "ul+[li(p(<a u>link</a>))]", "a link is no task");
        Case("- ```\n  code\n  ```", "ul+[li(code[](code))]", "a fence in an item");
        Case("-     code", "ul+[li(code[](code))]", "five blanks after the marker: indented code");
        Case("-\n  after", "ul+[li(p(after))]", "an item that starts on the next line");
        Case("-\n\n  not", "ul+[li()]; p(not)", "an item cannot start with two blank lines");
        Case("para\n2. not a list", "p(para 2. not a list)", "a numbered item not at 1 does not end a paragraph");
        Case("para\n1. a list", "p(para); ol1+[li(p(a list))]", "a numbered item at 1 ends a paragraph");
        Case("para\n- a list", "p(para); ul+[li(p(a list))]", "a bullet ends a paragraph");
        Case("- a\n* * *\n- b", "ul+[li(p(a))]; hr; ul+[li(p(b))]", "a rule ends a list");
        Case("-not a list", "p(-not a list)", "a marker without a blank is text");
        Case("1234567890. big", "p(1234567890. big)", "ten digits are not a marker");
        Case("- a\n\n\n- b", "ul-[li(p(a)) li(p(b))]", "two blank lines between items");
        Case("- a\n  ```\n\n  x\n  ```", "ul+[li(p(a); code[](\\nx))]", "a blank line inside a code block of an item keeps the list tight");
    }

    private static void Tables()
    {
        Case("| a | b |\n|---|:-:|\n| 1 | 2 |", "table[NC](a|b)(1|2)", "a table");
        Case("a | b\n:-- | --:\nx | y", "table[LR](a|b)(x|y)", "a table without the outer pipes");
        Case("| a |\n|---|\n| 1 | 2 | 3 |\n|", "table[N](a)(1)()", "rows cut and padded to the header");
        Case("| a | b |\n|---|---|\n| `x\\|y` | **z** |", "table[NN](a|b)({x|y}|<b>z</b>)", "an escaped pipe and inlines in cells");
        Case("| a | b |\n|---|\n| 1 | 2 |", "p(| a | b | |---| | 1 | 2 |)", "a separator of another column count is no table");
        Case("| a | b |\n|---|---|\n| 1 | 2 |\n\nafter", "table[NN](a|b)(1|2); p(after)", "a blank line ends a table");
        Case("| a | b |\n|---|---|\n> q", "table[NN](a|b); quote[p(q)]", "a quote ends a table");
        Case("text\n| a | b |\n| - | - |", "p(text); table[NN](a|b)", "a table after a paragraph");
        Case("a\n---", "h2#a(a)", "one column without pipes is a setext heading");
    }

    private static void Html()
    {
        Case("<div align=\"center\">\n  <img src=\"logo.png\" alt=\"Logo\">\n</div>", "html(<img logo.png|Logo>)", "an HTML block shows its images");
        Case("<p>one</p>\n<p>two</p>", "html(one\\ntwo)", "the closing tags of an HTML block break the lines");
        Case("<details>\n<summary>More</summary>\n\nHidden *text*\n\n</details>", "html(More); p(Hidden <em>text</em>)", "Markdown between HTML blocks");
        Case("<!-- a comment\nover lines -->\ntext", "p(text)", "an HTML comment shows nothing");
        Case("<!-- unclosed", "", "an unclosed comment shows nothing");
        Case("<pre>\n  a &lt; b\n</pre>", "code[](  a < b)", "<pre> is a code block");
        Case("<script>\nalert(1)\n</script>\nafter", "p(after)", "a script shows nothing");
        Case("<custom-tag>", "", "a tag alone on its line is an HTML block that shows nothing");
        Case("<b>bold</b> start", "p(<html>bold<html> start)", "an inline tag at the start is a paragraph");
        Case("para\n<custom-tag>", "p(para <html>)", "an unknown tag does not end a paragraph");
        Case("para\n<div>x</div>", "p(para); html(x)", "a block tag ends a paragraph");
    }

    private static void SourceLines()
    {
        MarkdownDocument document = MarkdownParser.Parse("# Title\n\npara one\npara two\n\n```\ncode\n```\n\n- a\n- b\n\n| x |\n|---|\n| 1 |\n");
        List<MarkdownBlock> blocks = document.Blocks;
        Equal(blocks.Count, 5, "five blocks");
        Equal(blocks[0].Line + "-" + blocks[0].EndLine, "0-0", "lines of the heading");
        Equal(blocks[1].Line + "-" + blocks[1].EndLine, "2-3", "lines of the paragraph");
        Equal(blocks[2].Line + "-" + blocks[2].EndLine, "5-7", "lines of the fenced code with its fences");
        Equal(blocks[3].Line + "-" + blocks[3].EndLine, "9-10", "lines of the list");
        Equal(blocks[3].Items[1].Line, 10, "the line of the second item");
        Equal(blocks[4].Line + "-" + blocks[4].EndLine, "12-14", "lines of the table");
        Equal(document.BlockIndexAtLine(0), 0, "line 0 is the heading");
        Equal(document.BlockIndexAtLine(1), 0, "a blank line after a block belongs to it");
        Equal(document.BlockIndexAtLine(3), 1, "the second line of the paragraph");
        Equal(document.BlockIndexAtLine(6), 2, "a line inside the code");
        Equal(document.BlockIndexAtLine(100), 4, "a line past the end is the last block");
        Equal(MarkdownParser.Parse("\n\ntext").BlockIndexAtLine(0), -1, "no block before the first one");
        MarkdownDocument crlf = MarkdownParser.Parse("a\r\n\r\nb\rc");
        Equal(crlf.Blocks.Count + ":" + crlf.Blocks[1].Line + "-" + crlf.Blocks[1].EndLine, "2:2-3", "CR LF and CR end lines");
        MarkdownDocument setext = MarkdownParser.Parse("x\n\nTitle\n---");
        Equal(setext.Blocks[1].Line + "-" + setext.Blocks[1].EndLine, "2-3", "a setext heading takes its underline");
        MarkdownDocument quote = MarkdownParser.Parse("> a\n>\n> b");
        Equal(quote.Blocks[0].Children[1].Line, 2, "the blocks of a quote keep the lines of the document");
        MarkdownDocument nested = MarkdownParser.Parse("- item\n  ## Deep\n");
        Check(nested.FindHeading("DEEP") != null && nested.FindHeading("deep").Line == 1, "a heading inside a list is found by its anchor");
        Check(nested.FindHeading("nothing") == null && nested.FindHeading("") == null, "no heading for an unknown anchor");
    }

    private static void Links()
    {
        string doc = "/home/me/project/README.md";
        MarkdownLinkTarget target = MarkdownLinks.Resolve("https://example.com/a#b", doc);
        Equal(target.Kind + " " + target.Url, "Web https://example.com/a#b", "a web address");
        Equal(MarkdownLinks.Resolve("mailto:a@b.c", doc).Kind, MarkdownLinkKind.Web, "mailto");
        Equal(MarkdownLinks.Resolve("FTP://host/x", doc).Kind, MarkdownLinkKind.Web, "the case of a scheme");
        Equal(MarkdownLinks.Resolve("www.example.com", doc).Url, "http://www.example.com", "www. without a scheme");
        Equal(MarkdownLinks.Resolve("//cdn.example.com/x", doc).Url, "https://cdn.example.com/x", "an address without a scheme");
        Equal(MarkdownLinks.Resolve("javascript:alert(1)", doc).Kind, MarkdownLinkKind.None, "another scheme opens nothing");
        Equal(MarkdownLinks.Resolve("file:///etc/passwd", doc).Kind, MarkdownLinkKind.None, "file: opens nothing");
        Equal(MarkdownLinks.Resolve("", doc).Kind, MarkdownLinkKind.None, "an empty link");
        target = MarkdownLinks.Resolve("#Getting%20Started", doc);
        Equal(target.Kind + " " + target.Anchor, "Anchor Getting Started", "a link to a heading");
        target = MarkdownLinks.Resolve("docs/guide.md", doc);
        Equal(target.Kind + " " + target.Path + " " + (target.Anchor ?? "-"), "File /home/me/project/docs/guide.md -", "a relative file");
        target = MarkdownLinks.Resolve("./docs/../CHANGES.md#v2?x", doc);
        Equal(target.Path + " " + target.Anchor, "/home/me/project/CHANGES.md v2?x", "dots, and the anchor after #");
        Equal(MarkdownLinks.Resolve("a.md?raw=1", doc).Path, "/home/me/project/a.md", "the query is dropped");
        Equal(MarkdownLinks.Resolve("../../../../x.md", doc).Path, "/x.md", ".. stops at the root");
        Equal(MarkdownLinks.Resolve("/etc/hosts", doc).Path, "/etc/hosts", "an absolute path");
        Equal(MarkdownLinks.Resolve("my%20file%C3%A9.md", doc).Path, "/home/me/project/my fileé.md", "percent escapes of UTF-8");
        Equal(MarkdownLinks.Resolve("docs/", doc).Path, "/home/me/project/docs/", "a folder keeps its slash");
        Equal(MarkdownLinks.Resolve("../x.md", "README.md").Kind, MarkdownLinkKind.None, "a relative document path cannot go above its start");
        Equal(MarkdownLinks.Resolve("x.md", "dir/README.md").Path, "dir/x.md", "a relative document path stays relative");
        Equal(MarkdownLinks.Resolve("x.md", null).Kind, MarkdownLinkKind.None, "no document path: a relative link opens nothing");
        Equal(MarkdownLinks.Resolve("C:/x.md", "/c:/Users/me/README.md").Path, "C:/x.md", "a drive letter is no scheme");
        Equal(MarkdownLinks.Resolve("sub\\x.md", doc).Path, "/home/me/project/sub/x.md", "backslashes are separators");
    }

    private static void CodeColors()
    {
        Equal(MarkdownCodeColoring.LanguageOf("bash"), SyntaxHighlighting.Language.Bash, "bash");
        Equal(MarkdownCodeColoring.LanguageOf("sh"), SyntaxHighlighting.Language.Bash, "sh by its extension");
        Equal(MarkdownCodeColoring.LanguageOf("Python title=\"x\""), SyntaxHighlighting.Language.Python, "the first word, the case ignored");
        Equal(MarkdownCodeColoring.LanguageOf("{.json}"), SyntaxHighlighting.Language.Json, "a word in braces with a dot");
        Equal(MarkdownCodeColoring.LanguageOf("yaml"), SyntaxHighlighting.Language.Yaml, "yaml");
        Equal(MarkdownCodeColoring.LanguageOf("Dockerfile"), SyntaxHighlighting.Language.Dockerfile, "a file name");
        Equal(MarkdownCodeColoring.LanguageOf(""), SyntaxHighlighting.Language.Plain, "no info string");
        Equal(MarkdownCodeColoring.LanguageOf("text"), SyntaxHighlighting.Language.Plain, "plain text");
        Equal(MarkdownCodeColoring.LanguageOf("../etc/passwd"), SyntaxHighlighting.Language.Plain, "a path is no language");

        TokenCache.LineTokenizer python = SyntaxHighlighting.GetTokenizer(SyntaxHighlighting.Language.Python);
        List<MarkdownCodeRun>[] lines = MarkdownCodeColoring.Color("def f():\n    return 'x'  # c\n", python);
        Equal(lines.Length, 3, "one list for each line, the last one empty");
        Equal(lines[2].Count, 0, "an empty line has no part");
        bool covered = true;
        for (int i = 0; i < 2; i++)
        {
            int pos = 0;
            foreach (MarkdownCodeRun run in lines[i])
            {
                covered &= run.Start == pos && run.End > run.Start;
                pos = run.End;
            }
            covered &= pos == (i == 0 ? "def f():".Length : "    return 'x'  # c".Length);
        }
        Check(covered, "the parts cover each line in order without gaps or overlaps");
        Check(lines[0][0].Type != 0 && lines[0][0].End == 3, "def is coloured as a keyword");
        bool hasComment = false;
        foreach (MarkdownCodeRun run in lines[1])
        {
            hasComment |= run.Start == "    return 'x'  ".Length && run.Type != 0;
        }
        Check(hasComment, "the comment is coloured");
        List<MarkdownCodeRun>[] plain = MarkdownCodeColoring.Color("a\nbc", null);
        Equal(plain[1].Count + ":" + plain[1][0].Start + "-" + plain[1][0].End + ":" + plain[1][0].Type, "1:0-2:0", "no tokenizer: the line in the plain colour");
        // A tokenizer that keeps a state over lines: a Python string of three quotes.
        List<MarkdownCodeRun>[] multi = MarkdownCodeColoring.Color("x = '''a\nb'''\ny", python);
        Check(multi[1].Count > 0 && multi[1][0].Type != 0, "the state of the tokenizer goes on to the next line");
    }

    private static void Robustness()
    {
        Random random = new Random(12345);
        const string alphabet = "*_~`[]()!<>&#-+=|:\\ \n\tab1.\"'/h";
        Stopwatch watch = Stopwatch.StartNew();
        for (int round = 0; round < 3000; round++)
        {
            int length = random.Next(0, 200);
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                text.Append(alphabet[random.Next(alphabet.Length)]);
            }
            MarkdownParser.Parse(text.ToString());
        }
        Check(true, "3000 random texts parse without an exception");
        // Deep nesting stays under the limits and does not overflow the stack.
        MarkdownDocument quotes = MarkdownParser.Parse(new string('>', 5000) + " deep");
        Check(quotes.Blocks.Count == 1, "5000 nested quotes");
        StringBuilder lists = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            lists.Append(new string(' ', i * 2)).Append("- x\n");
        }
        Check(MarkdownParser.Parse(lists.ToString()).Blocks.Count >= 1, "500 nested list levels");
        StringBuilder emphasis = new StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            emphasis.Append("*a ");
        }
        for (int i = 0; i < 5000; i++)
        {
            emphasis.Append(" a*");
        }
        Check(Depth(MarkdownParser.Parse(emphasis.ToString()).Blocks[0].Inlines) <= 33, "5000 nested emphases stay at most 32 deep");
        StringBuilder links = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            links.Append("[");
        }
        links.Append("x");
        for (int i = 0; i < 2000; i++)
        {
            links.Append("](u)");
        }
        MarkdownParser.Parse(links.ToString());
        MarkdownParser.Parse(new string('*', 20000) + "a" + new string('_', 20000));
        MarkdownParser.Parse(new string('`', 5000) + "a" + new string('`', 4999));
        MarkdownParser.Parse(string.Join("\n", new string[3000]).Replace("\n", "| a |\n|---|\n"));
        Check(watch.ElapsedMilliseconds < 20000, "the large texts parse in reasonable time (" + watch.ElapsedMilliseconds + " ms)");
    }

    private static int Depth(List<MarkdownInline> inlines)
    {
        int depth = 0;
        foreach (MarkdownInline inline in inlines)
        {
            depth = Math.Max(depth, inline.Children.Count == 0 ? (inline.Kind == MarkdownInlineKind.Text ? 0 : 1) : 1 + Depth(inline.Children));
        }
        return depth;
    }
}
