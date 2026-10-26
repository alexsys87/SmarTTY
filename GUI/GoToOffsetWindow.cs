using SmarTTY.TextEditing;
using System;
using System.Windows;
using System.Windows.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Asks for the byte offset to show in the hex view (Ctrl+G). Accepts decimal ("1234") and hexadecimal ("0x4D2")
/// offsets; the editor has the final say on the bytes that exist.
/// </summary>
internal sealed class GoToOffsetWindow : Window
{
    private readonly TextBox _offsetBox;

    private readonly int _length;

    /// <summary>The chosen offset, within the document.</summary>
    public int Offset { get; private set; }

    public GoToOffsetWindow(int currentOffset, int length)
    {
        _length = Math.Max(0, length);
        Title = "Go to Offset";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");
        WindowOwnership.Attach(this, WindowOwnership.ActiveWindow());

        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(10)
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Byte offset (decimal or 0x hexadecimal), 0 - 0x" + _length.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ":",
            Margin = new Thickness(0, 0, 0, 5)
        });
        _offsetBox = new TextBox
        {
            Text = "0x" + currentOffset.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
            MinWidth = 260
        };
        panel.Children.Add(_offsetBox);

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
            _offsetBox.Focus();
            _offsetBox.SelectAll();
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (!HexDocument.TryParseOffset(_offsetBox.Text, _length, out int offset))
        {
            MessageBoxes.Info("Enter an offset from 0 to 0x" + _length.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ", e.g. 1024 or 0x400.");
            _offsetBox.Focus();
            return;
        }
        Offset = offset;
        DialogResult = true;
    }
}
