using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// The preview of a Markdown file beside its text in the editor tab: the <see cref="MarkdownDocument"/> of the text (<see cref="MarkdownParser"/>)
/// shown as a WPF <see cref="FlowDocument"/> in the colours of the theme, with the code blocks in the colours of the syntax of the editor. The links
/// open: a web address in the browser, a heading of the document by scrolling to it, another file through <see cref="FileLinkClicked"/>. Images are
/// shown by <see cref="Images"/> (a file of the server or a data address; their alternative text until the picture is there, when it cannot be,
/// and for a web address, which is not loaded), at most as wide as the preview. The text can be selected and copied; Ctrl+wheel zooms.
/// </summary>
internal sealed class MarkdownPreview : FlowDocumentScrollViewer
{
    private const double BaseFontSize = 14;

    private static readonly FontFamily TextFont = new FontFamily("Segoe UI, Arial");

    private static readonly FontFamily CodeFont = new FontFamily("Consolas, Courier New");

    private static readonly double[] HeadingScale = { 2.0, 1.5, 1.25, 1.0, 0.875, 0.85 };

    private readonly FlowDocument _document;

    // The WPF block of each block of the document (top level), and of every heading at any depth (for the links to it).
    private readonly List<Block> _blocks = new List<Block>();

    private readonly Dictionary<MarkdownBlock, Block> _headings = new Dictionary<MarkdownBlock, Block>();

    private MarkdownDocument _model = new MarkdownDocument();

    private int _shownBlock = -1;

    private SolidColorBrush _codeBackground;

    private SolidColorBrush _codeForeground;

    private SolidColorBrush[] _tokenBrushes = new SolidColorBrush[0];

    // The images shown, made narrower when the preview is (their width as the document wants it is in their Tag).
    private readonly List<Image> _images = new List<Image>();

    // Counts the drawings of the document: a picture that arrives for an earlier one is not put in.
    private int _generation;

    public MarkdownPreview()
    {
        _document = new FlowDocument
        {
            FontFamily = TextFont,
            FontSize = BaseFontSize,
            PagePadding = new Thickness(16, 12, 16, 16),
            TextAlignment = TextAlignment.Left
        };
        _document.SetResourceReference(FlowDocument.ForegroundProperty, "Theme.Text");
        _document.SetResourceReference(FlowDocument.BackgroundProperty, "Theme.ContentBackground");
        SetResourceReference(BackgroundProperty, "Theme.ContentBackground");
        Document = _document;
        IsToolBarVisible = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = true;
        SizeChanged += (sender, e) => FitImages();
    }

    /// <summary>The pictures of the images; null: an image is shown as its alternative text.</summary>
    public MarkdownImageLoader Images { get; set; }

    /// <summary>The path of the file on the server: the relative links are taken from its folder.</summary>
    public string DocumentPath { get; set; }

    /// <summary>A link to another file was clicked: its full path and the anchor after # (or null).</summary>
    public event Action<string, string> FileLinkClicked;

    /// <summary>The document that is shown.</summary>
    public MarkdownDocument Model => _model;

    /// <summary>
    /// The colours of the code blocks: the background and the text of the editor and the colours of its token types (the first for the plain text;
    /// none: the code is in the colour of the text).
    /// </summary>
    public void SetCodeColors(RgbColor background, RgbColor foreground, RgbColor[] tokenColors)
    {
        _codeBackground = background.IsEmpty ? null : Freeze(new SolidColorBrush(ToColor(background)));
        _codeForeground = foreground.IsEmpty ? null : Freeze(new SolidColorBrush(ToColor(foreground)));
        tokenColors = tokenColors ?? new RgbColor[0];
        _tokenBrushes = new SolidColorBrush[tokenColors.Length];
        for (int i = 0; i < tokenColors.Length; i++)
        {
            _tokenBrushes[i] = Freeze(new SolidColorBrush(ToColor(tokenColors[i])));
        }
    }

    /// <summary>Shows a Markdown text, keeping the place the preview is scrolled to.</summary>
    public void ShowText(string markdown)
    {
        MarkdownDocument model;
        try
        {
            model = MarkdownParser.Parse(markdown);
        }
        catch (Exception exception)
        {
            // The parser is meant never to throw; if it does, the preview says so instead of taking the tab down.
            model = MarkdownParser.Parse("```\n" + exception.Message + "\n```");
        }
        ScrollViewer scroll = FindScrollViewer();
        double offset = scroll?.VerticalOffset ?? 0;
        _model = model;
        _blocks.Clear();
        _headings.Clear();
        _images.Clear();
        _generation++;
        _shownBlock = -1;
        _document.Blocks.Clear();
        foreach (MarkdownBlock block in model.Blocks)
        {
            Block shown = BuildBlock(block, false, 0);
            _blocks.Add(shown);
            _document.Blocks.Add(shown);
        }
        if (scroll != null && offset > 0)
        {
            // The new blocks are laid out first.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => scroll.ScrollToVerticalOffset(offset)));
        }
    }

    /// <summary>Brings the block that shows a line of the source into view (when it is another block than the last time).</summary>
    public void ShowLine(int line)
    {
        int index = _model.BlockIndexAtLine(line);
        if (index < 0 || index >= _blocks.Count || index == _shownBlock)
        {
            return;
        }
        _shownBlock = index;
        Block block = _blocks[index];
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => block.BringIntoView()));
    }

    /// <summary>Scrolls to the heading of an anchor (<c>#usage</c>); false when the document has none of that name.</summary>
    public bool ShowAnchor(string anchor)
    {
        MarkdownBlock heading = _model.FindHeading(anchor);
        if (heading == null || !_headings.TryGetValue(heading, out Block block))
        {
            return false;
        }
        // The heading at the top, not only somewhere in view: from the end, bringing it into view scrolls up until it is the first line.
        ScrollViewer scroll = FindScrollViewer();
        if (scroll != null)
        {
            scroll.ScrollToEnd();
            UpdateLayout();
        }
        block.BringIntoView();
        _shownBlock = -1;
        return true;
    }

    private ScrollViewer FindScrollViewer()
    {
        ApplyTemplate();
        return Template?.FindName("PART_ContentHost", this) as ScrollViewer;
    }

    // ---------------------------------------------------------------- blocks

    private Block BuildBlock(MarkdownBlock block, bool tight, int listDepth)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
                return BuildHeading(block);
            case MarkdownBlockKind.Code:
                return BuildCode(block);
            case MarkdownBlockKind.Quote:
            {
                Section quote = new Section
                {
                    BorderThickness = new Thickness(4, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 12)
                };
                quote.SetResourceReference(Section.BorderBrushProperty, "Theme.BorderStrong");
                quote.SetResourceReference(Section.ForegroundProperty, "Theme.TextSecondary");
                AddBlocks(quote.Blocks, block.Children, false, listDepth);
                return quote;
            }
            case MarkdownBlockKind.List:
                return BuildList(block, listDepth);
            case MarkdownBlockKind.Table:
                return BuildTable(block);
            case MarkdownBlockKind.Rule:
            {
                Paragraph rule = new Paragraph
                {
                    FontSize = 1,
                    LineHeight = 1,
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    Margin = new Thickness(0, 12, 0, 12)
                };
                rule.SetResourceReference(Paragraph.BorderBrushProperty, "Theme.Divider");
                return rule;
            }
            default:
            {
                // A paragraph, or the text of an HTML block.
                Paragraph paragraph = new Paragraph { Margin = tight ? new Thickness(0) : new Thickness(0, 0, 0, 12) };
                AddInlines(paragraph.Inlines, block.Inlines, null);
                return paragraph;
            }
        }
    }

    private void AddBlocks(BlockCollection target, List<MarkdownBlock> blocks, bool tight, int listDepth)
    {
        foreach (MarkdownBlock block in blocks)
        {
            target.Add(BuildBlock(block, tight, listDepth));
        }
    }

    private Block BuildHeading(MarkdownBlock block)
    {
        int level = Math.Max(1, Math.Min(6, block.Level));
        Paragraph heading = new Paragraph
        {
            FontSize = BaseFontSize * HeadingScale[level - 1],
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level <= 2 ? 18 : 14, 0, 8)
        };
        if (level <= 2)
        {
            heading.BorderThickness = new Thickness(0, 0, 0, 1);
            heading.Padding = new Thickness(0, 0, 0, 4);
            heading.SetResourceReference(Paragraph.BorderBrushProperty, "Theme.Divider");
        }
        if (level == 6)
        {
            heading.SetResourceReference(Paragraph.ForegroundProperty, "Theme.TextSecondary");
        }
        AddInlines(heading.Inlines, block.Inlines, null);
        _headings[block] = heading;
        return heading;
    }

    private Block BuildCode(MarkdownBlock block)
    {
        Paragraph code = new Paragraph
        {
            FontFamily = CodeFont,
            FontSize = BaseFontSize * 0.9,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 12),
            TextAlignment = TextAlignment.Left
        };
        if (_codeBackground != null && _codeForeground != null)
        {
            code.Background = _codeBackground;
            code.Foreground = _codeForeground;
        }
        else
        {
            code.SetResourceReference(Paragraph.BackgroundProperty, "Theme.SurfaceAltBackground");
        }
        string text = block.Text ?? "";
        List<MarkdownCodeRun>[] lines = ColorCode(text, block.Info);
        string[] lineTexts = text.Split('\n');
        for (int i = 0; i < lineTexts.Length; i++)
        {
            if (i > 0)
            {
                code.Inlines.Add(new LineBreak());
            }
            string line = lineTexts[i];
            List<MarkdownCodeRun> runs = (lines != null && i < lines.Length) ? lines[i] : null;
            if (runs == null || runs.Count == 0)
            {
                code.Inlines.Add(new Run(ExpandTabs(line)));
                continue;
            }
            foreach (MarkdownCodeRun run in runs)
            {
                Run shown = new Run(ExpandTabs(line.Substring(run.Start, run.End - run.Start)));
                int color = SyntaxColoring.ColorIndex(run.Type, _tokenBrushes.Length);
                if (color > 0)
                {
                    shown.Foreground = _tokenBrushes[color];
                }
                code.Inlines.Add(shown);
            }
        }
        return code;
    }

    // The coloured parts of the lines of a code block, or null when the block names no language or the colours are not known.
    private List<MarkdownCodeRun>[] ColorCode(string text, string info)
    {
        if (_tokenBrushes.Length == 0 || string.IsNullOrWhiteSpace(info))
        {
            return null;
        }
        try
        {
            TokenCache.LineTokenizer tokenizer = SyntaxHighlighting.GetTokenizer(MarkdownCodeColoring.LanguageOf(info));
            return tokenizer == null ? null : MarkdownCodeColoring.Color(text, tokenizer);
        }
        catch (Exception)
        {
            // A tokenizer that fails on a text: the code in the plain colour.
            return null;
        }
    }

    private static string ExpandTabs(string text)
    {
        return text.Replace("\t", "    ");
    }

    private Block BuildList(MarkdownBlock block, int depth)
    {
        List list = new List
        {
            MarkerStyle = block.Ordered ? TextMarkerStyle.Decimal : (depth % 3 == 0) ? TextMarkerStyle.Disc : (depth % 3 == 1) ? TextMarkerStyle.Circle : TextMarkerStyle.Square,
            StartIndex = Math.Max(1, block.Start),
            Padding = new Thickness(24, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, depth == 0 ? 12 : 0)
        };
        foreach (MarkdownListItem item in block.Items)
        {
            ListItem shown = new ListItem();
            AddBlocks(shown.Blocks, item.Blocks, block.Tight, depth + 1);
            if (item.Checked.HasValue)
            {
                Paragraph first = shown.Blocks.FirstBlock as Paragraph;
                if (first == null)
                {
                    first = new Paragraph { Margin = block.Tight ? new Thickness(0) : new Thickness(0, 0, 0, 12) };
                    if (shown.Blocks.FirstBlock == null)
                    {
                        shown.Blocks.Add(first);
                    }
                    else
                    {
                        shown.Blocks.InsertBefore(shown.Blocks.FirstBlock, first);
                    }
                }
                Run box = new Run(item.Checked.Value ? "☑ " : "☐ ");
                if (first.Inlines.FirstInline == null)
                {
                    first.Inlines.Add(box);
                }
                else
                {
                    first.Inlines.InsertBefore(first.Inlines.FirstInline, box);
                }
            }
            if (shown.Blocks.FirstBlock == null)
            {
                shown.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
            }
            list.ListItems.Add(shown);
        }
        return list;
    }

    private Block BuildTable(MarkdownBlock block)
    {
        Table table = new Table
        {
            CellSpacing = 0,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 0, 0, 12)
        };
        table.SetResourceReference(Table.BorderBrushProperty, "Theme.Border");
        for (int i = 0; i < block.Alignments.Count; i++)
        {
            table.Columns.Add(new TableColumn());
        }
        TableRowGroup group = new TableRowGroup();
        TableRow header = BuildRow(block.Header, block.Alignments);
        header.FontWeight = FontWeights.SemiBold;
        header.SetResourceReference(TableRow.BackgroundProperty, "Theme.SurfaceAltBackground");
        group.Rows.Add(header);
        foreach (List<List<MarkdownInline>> row in block.Rows)
        {
            group.Rows.Add(BuildRow(row, block.Alignments));
        }
        table.RowGroups.Add(group);
        return table;
    }

    private TableRow BuildRow(List<List<MarkdownInline>> cells, List<MarkdownAlignment> alignments)
    {
        TableRow row = new TableRow();
        for (int i = 0; i < cells.Count; i++)
        {
            Paragraph text = new Paragraph { Margin = new Thickness(0) };
            AddInlines(text.Inlines, cells[i], null);
            MarkdownAlignment alignment = (i < alignments.Count) ? alignments[i] : MarkdownAlignment.None;
            text.TextAlignment = alignment == MarkdownAlignment.Center ? TextAlignment.Center
                : alignment == MarkdownAlignment.Right ? TextAlignment.Right : TextAlignment.Left;
            TableCell cell = new TableCell(text)
            {
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(8, 4, 8, 4)
            };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "Theme.Border");
            row.Cells.Add(cell);
        }
        return row;
    }

    // ---------------------------------------------------------------- inlines

    // linkUrl: the target of the link the inlines are in, or null.
    private void AddInlines(InlineCollection target, List<MarkdownInline> inlines, string linkUrl)
    {
        foreach (MarkdownInline inline in inlines)
        {
            Inline shown = BuildInline(inline, linkUrl);
            if (shown != null)
            {
                target.Add(shown);
            }
        }
    }

    private Inline BuildInline(MarkdownInline inline, string linkUrl)
    {
        switch (inline.Kind)
        {
            case MarkdownInlineKind.Text:
                return new Run(inline.Text);
            case MarkdownInlineKind.Code:
            {
                Run code = new Run(inline.Text) { FontFamily = CodeFont, FontSize = BaseFontSize * 0.9 };
                code.SetResourceReference(Run.BackgroundProperty, "Theme.SurfaceAltBackground");
                return code;
            }
            case MarkdownInlineKind.Emphasis:
            {
                Italic italic = new Italic();
                AddInlines(italic.Inlines, inline.Children, linkUrl);
                return italic;
            }
            case MarkdownInlineKind.Strong:
            {
                Bold bold = new Bold();
                AddInlines(bold.Inlines, inline.Children, linkUrl);
                return bold;
            }
            case MarkdownInlineKind.Strikethrough:
            {
                Span struck = new Span { TextDecorations = TextDecorations.Strikethrough };
                AddInlines(struck.Inlines, inline.Children, linkUrl);
                return struck;
            }
            case MarkdownInlineKind.Link:
            {
                if (linkUrl != null)
                {
                    // A link cannot hold another one: its text only.
                    Span span = new Span();
                    AddInlines(span.Inlines, inline.Children, linkUrl);
                    return span;
                }
                Hyperlink link = new Hyperlink();
                link.SetResourceReference(Hyperlink.ForegroundProperty, "Theme.Link");
                AddInlines(link.Inlines, inline.Children, inline.Url ?? "");
                ConnectLink(link, inline.Url, inline.Title);
                return link;
            }
            case MarkdownInlineKind.Image:
                return BuildImage(inline, linkUrl);
            case MarkdownInlineKind.SoftBreak:
                return new Run(" ");
            case MarkdownInlineKind.LineBreak:
                return new LineBreak();
            default:
                return null;
        }
    }

    // An image: its picture when it is loaded, otherwise its alternative text, which the picture replaces when it arrives.
    private Inline BuildImage(MarkdownInline inline, string linkUrl)
    {
        Span holder = new Span();
        int generation = _generation;
        MarkdownImageLoader.Entry entry = Images?.Request(inline.Url, DocumentPath, loaded =>
        {
            if (generation != _generation)
            {
                return;
            }
            holder.Inlines.Clear();
            holder.Inlines.Add(ImageOrText(inline, loaded, linkUrl));
        });
        holder.Inlines.Add(ImageOrText(inline, entry, linkUrl));
        return holder;
    }

    private Inline ImageOrText(MarkdownInline inline, MarkdownImageLoader.Entry entry, string linkUrl)
    {
        if (entry != null && entry.State == MarkdownImageLoader.State.Loaded)
        {
            return ImageInline(inline, entry, linkUrl);
        }
        string alt = string.IsNullOrWhiteSpace(inline.Text) ? ImageName(inline.Url) : inline.Text;
        Run picture = new Run("[\U0001F5BC " + alt + "]");
        picture.SetResourceReference(Run.ForegroundProperty, "Theme.TextSecondary");
        string tip = inline.Url;
        if (entry != null && entry.State == MarkdownImageLoader.State.Failed)
        {
            tip = inline.Url + "\n" + entry.Error;
        }
        else if (entry != null)
        {
            tip = inline.Url + "\nLoading...";
        }
        if (linkUrl != null)
        {
            return picture;
        }
        picture.ToolTip = tip;
        // A picture that is not shown opens in the browser when it is on the web.
        MarkdownLinkTarget target = MarkdownLinks.Resolve(inline.Url, DocumentPath);
        if (target.Kind != MarkdownLinkKind.Web)
        {
            return picture;
        }
        Hyperlink link = new Hyperlink(picture);
        link.SetResourceReference(Hyperlink.ForegroundProperty, "Theme.Link");
        ConnectLink(link, inline.Url, inline.Title);
        link.ToolTip = tip;
        return link;
    }

    private Inline ImageInline(MarkdownInline inline, MarkdownImageLoader.Entry entry, string linkUrl)
    {
        double wanted = MarkdownImages.ShownWidth(entry.Width, entry.Height, inline.Width, inline.Height, 0);
        Image image = new Image
        {
            Source = entry.Image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            Width = MarkdownImages.ShownWidth(entry.Width, entry.Height, inline.Width, inline.Height, AvailableWidth()),
            Tag = wanted,
            ToolTip = string.IsNullOrEmpty(inline.Title) ? (string.IsNullOrEmpty(inline.Text) ? inline.Url : inline.Text) : inline.Title,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        if (!string.IsNullOrEmpty(linkUrl))
        {
            // A click on a picture inside a link follows the link (a picture in a Hyperlink takes the mouse itself).
            image.Cursor = System.Windows.Input.Cursors.Hand;
            // The press is the picture's, so that the link does not take the mouse and open it a second time.
            image.MouseLeftButtonDown += (sender, e) => e.Handled = true;
            image.MouseLeftButtonUp += (sender, e) =>
            {
                e.Handled = true;
                OpenLink(linkUrl);
            };
        }
        _images.Add(image);
        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
    }

    // The width an image may take: the preview less its margins and a scroll bar.
    private double AvailableWidth()
    {
        double width = ActualWidth - _document.PagePadding.Left - _document.PagePadding.Right - SystemParameters.VerticalScrollBarWidth - 8;
        return width > 32 ? width : 0;
    }

    private void FitImages()
    {
        double available = AvailableWidth();
        foreach (Image image in _images)
        {
            double wanted = image.Tag is double width ? width : image.Width;
            image.Width = (available > 0 && wanted > available) ? available : wanted;
        }
    }

    private static string ImageName(string url)
    {
        string name = (url ?? "").TrimEnd('/');
        int slash = name.LastIndexOf('/');
        return (slash >= 0) ? name.Substring(slash + 1) : name;
    }

    private void ConnectLink(Hyperlink link, string url, string title)
    {
        link.ToolTip = string.IsNullOrEmpty(title) ? url : title + "\n" + url;
        link.Click += (sender, e) =>
        {
            e.Handled = true;
            OpenLink(url);
        };
    }

    private void OpenLink(string url)
    {
        MarkdownLinkTarget target = MarkdownLinks.Resolve(url, DocumentPath);
        switch (target.Kind)
        {
            case MarkdownLinkKind.Web:
                UrlOpener.Open(target.Url);
                break;
            case MarkdownLinkKind.Anchor:
                ShowAnchor(target.Anchor);
                break;
            case MarkdownLinkKind.File:
                if (target.Anchor != null && string.Equals(target.Path, DocumentPath, StringComparison.Ordinal))
                {
                    ShowAnchor(target.Anchor);
                }
                else
                {
                    FileLinkClicked?.Invoke(target.Path, target.Anchor);
                }
                break;
        }
    }

    private static Color ToColor(RgbColor color)
    {
        return Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
