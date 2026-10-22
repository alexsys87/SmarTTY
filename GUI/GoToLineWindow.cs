using System;
using System.Windows;
using System.Windows.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Asks for the line to go to in the file editor (Ctrl+G). Accepts "line" or "line:column", both 1-based.
/// </summary>
internal class GoToLineWindow : Window
{
    private readonly TextBox _lineBox;

    private readonly int _lineCount;

    /// <summary>The chosen line, 1-based and within the document.</summary>
    public int Line { get; private set; }

    /// <summary>The chosen column, 1-based (1 when only a line was entered).</summary>
    public int Column { get; private set; } = 1;

    public GoToLineWindow(int currentLine, int lineCount)
    {
        _lineCount = lineCount;
        Title = "Go to Line";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");

        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(10)
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Line number (1 - " + lineCount + "), optionally followed by :column:",
            Margin = new Thickness(0, 0, 0, 5)
        });
        _lineBox = new TextBox
        {
            Text = currentLine.ToString(),
            MinWidth = 260
        };
        panel.Children.Add(_lineBox);

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Button ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            MinWidth = 75,
            Margin = new Thickness(0, 0, 5, 0)
        };
        ok.Click += OK_Click;
        Button cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 75
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (object sender, RoutedEventArgs e) =>
        {
            _lineBox.Focus();
            _lineBox.SelectAll();
        };
    }

    /// <summary>Parses "line" or "line:column"; returns false for anything else.</summary>
    internal static bool TryParse(string text, int lineCount, out int line, out int column)
    {
        line = 0;
        column = 1;
        string[] parts = (text ?? "").Trim().Split(':');
        if (parts.Length > 2 || !int.TryParse(parts[0].Trim(), out line) || line < 1)
        {
            return false;
        }
        if (parts.Length == 2 && (!int.TryParse(parts[1].Trim(), out column) || column < 1))
        {
            return false;
        }
        line = Math.Min(line, Math.Max(1, lineCount));
        return true;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParse(_lineBox.Text, _lineCount, out int line, out int column))
        {
            MessageBoxes.Info("Enter a line number from 1 to " + _lineCount + ", e.g. 120 or 120:5.");
            _lineBox.Focus();
            return;
        }
        Line = line;
        Column = column;
        DialogResult = true;
    }
}
