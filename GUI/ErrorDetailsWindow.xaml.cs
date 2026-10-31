using System;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// An error dialog whose text can be selected and copied: unlike <see cref="MessageDialog"/> it shows the message
/// (for example the output of a failed command) in a read-only text box, with a "Copy all" button.
/// </summary>
public partial class ErrorDetailsWindow : Window
{
    private readonly string _Text;

    private ErrorDetailsWindow(string text)
    {
        InitializeComponent();
        _Text = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        // The first line is the summary, the rest is the details (the output of the command).
        int newLine = _Text.IndexOf(Environment.NewLine, StringComparison.Ordinal);
        HeaderText.Text = newLine > 0 ? _Text.Substring(0, newLine) : "An error occurred";
        DetailsBox.Text = _Text;
        Loaded += (sender, e) => DetailsBox.Focus();
    }

    /// <summary>Shows the dialog; safe to call from a background thread (the window is created on the UI thread).</summary>
    internal static void Show(string text, Window owner = null)
    {
        Application application = Application.Current;
        if (application != null && !application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.Invoke(() => ShowOnUiThread(text, owner));
            return;
        }
        ShowOnUiThread(text, owner);
    }

    private static void ShowOnUiThread(string text, Window owner)
    {
        ErrorDetailsWindow window = new ErrorDetailsWindow(text);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        window.ShowDialog();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_Text);
            CopiedText.Text = "Copied to the clipboard";
        }
        catch (Exception exception)
        {
            // The clipboard can be locked by another process for a moment.
            CopiedText.Text = "Could not copy: " + exception.Message;
        }
        CopiedText.Visibility = Visibility.Visible;
    }
}
