using System.Collections.Generic;
using System.Windows;
using SmarTTY.Controls;
using SmarTTY.Utilities;

namespace SmarTTY.GUI;

/// <summary>
/// Asks whether text that is about to be pasted into a terminal should really go there: shows what it is (lines,
/// characters, a final line break), a preview, and what is dangerous in it. The WPF counterpart of
/// <c>Forms/PasteConfirmForm</c>.
/// </summary>
public partial class PasteConfirmWindow : Window
{
    private PasteConfirmWindow(string summary, string preview, IList<string> dangers, bool offerDontAsk)
    {
        InitializeComponent();
        bool dangerous = dangers != null && dangers.Count > 0;
        Title = PasteConfirmRules.Title(dangerous);
        SummaryText.Text = summary;
        string dangerLine = PasteConfirmRules.DangerLine(dangers);
        if (dangerLine.Length > 0)
        {
            DangerText.Text = dangerLine;
            DangerText.Visibility = Visibility.Visible;
        }
        PreviewBox.Text = preview ?? "";
        if (offerDontAsk)
        {
            DontAskCheck.Visibility = Visibility.Visible;
        }
        // Enter must not paste dangerous text by accident: then the safe answer (Cancel) is the default.
        if (PasteConfirmRules.DefaultToCancel(dangerous))
        {
            CancelButton.IsDefault = true;
            Loaded += (sender, e) => CancelButton.Focus();
        }
        else
        {
            PasteButton.IsDefault = true;
            Loaded += (sender, e) => PasteButton.Focus();
        }
    }

    /// <summary>True when the user asked not to be asked about multi-line pastes again.</summary>
    public bool DontAskAgain => DontAskCheck.Visibility == Visibility.Visible && DontAskCheck.IsChecked == true;

    /// <summary>
    /// Shows the confirmation. Returns the answer, or null when the user cancelled.
    /// </summary>
    internal static ThemedPasteConfirmResult Confirm(Window owner, string summary, string preview, IList<string> dangers, bool offerDontAsk)
    {
        PasteConfirmWindow window = new PasteConfirmWindow(summary, preview, dangers, offerDontAsk);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return null;
        }
        return new ThemedPasteConfirmResult { DontAskAgain = window.DontAskAgain };
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
