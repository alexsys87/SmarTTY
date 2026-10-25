using System;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Shows the warnings and errors collected during a directory transfer. When the transfer also failed, the message
/// of the exception is shown above the log.
/// </summary>
public partial class TransferErrorWindow : Window
{
    private TransferErrorWindow(Exception exception, string text)
    {
        InitializeComponent();
        ErrorsBox.Text = text;
        if (exception != null)
        {
            IntroText.Text = "Directory transfer failed with the following error: " + exception.Message;
        }
    }

    /// <summary>Shows the log of a finished directory transfer over the window that started it.</summary>
    public static void Show(Exception exception, string text, Window owner)
    {
        TransferErrorWindow window = new TransferErrorWindow(exception, text);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        window.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
