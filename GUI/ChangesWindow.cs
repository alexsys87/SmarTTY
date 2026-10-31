using SmarTTY.CodeEditing;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SmarTTY.GUI;

/// <summary>
/// Shows what the file editor has changed against the file on the server, as a unified diff (Show Changes).
/// </summary>
internal class ChangesWindow : Window
{
    // A very long diff would make the document slow to build and to scroll; the rest is left out with a note.
    private const int MaxShownLines = 5000;

    private static readonly Brush AddedBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x45, 0x2E, 0xA0, 0x43)));

    private static readonly Brush RemovedBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x45, 0xD0, 0x40, 0x40)));

    private static readonly Brush HunkBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x35, 0x40, 0x80, 0xD0)));

    private readonly string _unifiedText;

    public ChangesWindow(string fileName, TextDiff.Result changes)
    {
        _unifiedText = changes.UnifiedText;
        Title = "Changes in " + fileName;
        Width = 900;
        Height = 600;
        MinWidth = 400;
        MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");

        Grid layout = new Grid
        {
            Margin = new Thickness(10)
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock summary = new TextBlock
        {
            Text = Summarize(changes),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(summary, 0);
        layout.Children.Add(summary);

        RichTextBox view = new RichTextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        view.SetResourceReference(Control.BackgroundProperty, "Theme.WindowBackground");
        view.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        view.SetResourceReference(Control.BorderBrushProperty, "Theme.Border");
        view.Document = BuildDocument(changes);
        Grid.SetRow(view, 1);
        layout.Children.Add(view);

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Button copy = new Button
        {
            Content = "Copy",
            MinWidth = 75,
            Margin = new Thickness(0, 0, 5, 0),
            IsEnabled = !changes.IsIdentical,
            ToolTip = "Copy the diff to the clipboard"
        };
        copy.Click += (object sender, RoutedEventArgs e) =>
        {
            try
            {
                Clipboard.SetText(_unifiedText);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // The clipboard is busy; nothing was copied and the user can press the button again.
            }
        };
        Button close = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 75
        };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);

        Content = layout;
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static string Summarize(TextDiff.Result changes)
    {
        if (changes.IsIdentical)
        {
            return changes.LineEndingsDiffer
                ? "The text has the same lines as the file on the server; only the line endings differ."
                : "The text is the same as the file on the server.";
        }
        string text = changes.AddedLines + (changes.AddedLines == 1 ? " line added, " : " lines added, ")
            + changes.RemovedLines + (changes.RemovedLines == 1 ? " line removed" : " lines removed")
            + " against the file on the server.";
        if (changes.LineEndingsDiffer)
        {
            text += " The line endings differ too (not shown).";
        }
        if (changes.Coarse)
        {
            text += " There are too many differences to compare line by line, so the changed part is shown as replaced.";
        }
        return text;
    }

    private static FlowDocument BuildDocument(TextDiff.Result changes)
    {
        FlowDocument document = new FlowDocument
        {
            PagePadding = new Thickness(4),
            // The lines are not wrapped; the view scrolls sideways.
            PageWidth = 100000
        };
        if (changes.IsIdentical)
        {
            return document;
        }
        string[] lines = changes.UnifiedText.TrimEnd('\n').Split('\n');
        int shown = Math.Min(lines.Length, MaxShownLines);
        for (int i = 0; i < shown; i++)
        {
            string line = lines[i];
            Paragraph paragraph = new Paragraph(new Run(line))
            {
                Margin = new Thickness(0)
            };
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                paragraph.Background = HunkBackground;
            }
            else if (i >= 2 && line.StartsWith("+", StringComparison.Ordinal))
            {
                paragraph.Background = AddedBackground;
            }
            else if (i >= 2 && line.StartsWith("-", StringComparison.Ordinal))
            {
                paragraph.Background = RemovedBackground;
            }
            else if (i < 2)
            {
                paragraph.FontWeight = FontWeights.Bold;
            }
            document.Blocks.Add(paragraph);
        }
        if (shown < lines.Length)
        {
            document.Blocks.Add(new Paragraph(new Run("... " + (lines.Length - shown) + " more lines are not shown (use Copy to get the whole diff)."))
            {
                Margin = new Thickness(0, 6, 0, 0),
                FontStyle = FontStyles.Italic
            });
        }
        return document;
    }
}
