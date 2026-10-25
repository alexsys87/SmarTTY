using System.Windows;
using System.Windows.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Asks for the name of a new or renamed file or folder in the file list. Built in code, like
/// <see cref="GoToLineWindow"/>.
/// </summary>
internal class RemoteNameWindow : Window
{
    private readonly TextBox _name;

    /// <summary>The entered name.</summary>
    public string EnteredName => _name.Text;

    private RemoteNameWindow(string title, string prompt, string initialName, bool selectStemOnly)
    {
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");

        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(12)
        };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        _name = new TextBox
        {
            Text = initialName ?? ""
        };
        panel.Children.Add(_name);

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Button ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            MinWidth = 80,
            Margin = new Thickness(0, 0, 5, 0)
        };
        ok.Click += (object sender, RoutedEventArgs e) => DialogResult = true;
        Button cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (object sender, RoutedEventArgs e) =>
        {
            _name.Focus();
            // As in Explorer, renaming "report.txt" selects "report" so that typing keeps the extension.
            int stemLength = _name.Text.Length;
            if (selectStemOnly)
            {
                int dot = _name.Text.LastIndexOf('.');
                if (dot > 0)
                {
                    stemLength = dot;
                }
            }
            _name.Select(0, stemLength);
        };
    }

    /// <summary>Shows the dialog; returns the entered name, or null if it was cancelled.</summary>
    internal static string Ask(Window owner, string title, string prompt, string initialName, bool selectStemOnly = false)
    {
        RemoteNameWindow window = new RemoteNameWindow(title, prompt, initialName, selectStemOnly);
        WindowOwnership.Attach(window, owner);
        return window.ShowDialog() == true ? window.EnteredName : null;
    }
}
