using SmarTTY.IO;
using System;
using System.Windows;
using System.Windows.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Asks how to follow a log file (Follow Log): how many of its last lines to show first, and an optional filter.
/// </summary>
internal class FollowLogWindow : Window
{
    private readonly TextBox _linesBox;

    private readonly TextBox _filterBox;

    private readonly CheckBox _regexBox;

    private readonly CheckBox _ignoreCaseBox;

    public int Lines { get; private set; } = RemoteLogFollow.DefaultLines;

    /// <summary>The text that a line has to contain to be shown; empty for every line.</summary>
    public string Filter { get; private set; } = "";

    public bool FilterIsRegex { get; private set; }

    public bool IgnoreCase { get; private set; } = true;

    public FollowLogWindow(string fileName)
    {
        Title = "Follow Log";
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
            Text = "Follow " + fileName + " like tail -f in a new terminal tab (Ctrl+C ends it).",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Show the last lines first (0 - " + RemoteLogFollow.MaxLines + "):",
            Margin = new Thickness(0, 0, 0, 3)
        });
        _linesBox = new TextBox
        {
            Text = RemoteLogFollow.DefaultLines.ToString(),
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        panel.Children.Add(_linesBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Only the lines that contain (empty: all lines):",
            Margin = new Thickness(0, 8, 0, 3)
        });
        _filterBox = new TextBox
        {
            MinWidth = 380
        };
        panel.Children.Add(_filterBox);
        _regexBox = new CheckBox
        {
            Content = "Regular expression (grep -E)",
            Margin = new Thickness(0, 6, 0, 0)
        };
        panel.Children.Add(_regexBox);
        _ignoreCaseBox = new CheckBox
        {
            Content = "Ignore case",
            IsChecked = true,
            Margin = new Thickness(0, 3, 0, 0)
        };
        panel.Children.Add(_ignoreCaseBox);

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Button ok = new Button
        {
            Content = "Follow",
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
            _filterBox.Focus();
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (!RemoteLogFollow.TryParseLines(_linesBox.Text, out int lines))
        {
            MessageBoxes.Info("Enter a number of lines from 0 to " + RemoteLogFollow.MaxLines + ".");
            _linesBox.Focus();
            _linesBox.SelectAll();
            return;
        }
        Lines = lines;
        Filter = _filterBox.Text ?? "";
        FilterIsRegex = _regexBox.IsChecked == true;
        IgnoreCase = _ignoreCaseBox.IsChecked != false;
        DialogResult = true;
    }
}
