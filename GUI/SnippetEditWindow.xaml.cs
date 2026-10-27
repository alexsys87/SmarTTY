using System.Windows;
using SmarTTY.Controls;
using SmarTTY.Utilities;

namespace SmarTTY.GUI;

/// <summary>
/// Adds or edits a snippet of the paste menu: a name and the text. Delete (only when the snippet exists already)
/// closes the window asking for the removal. The WPF counterpart of <c>Forms/SnippetEditForm</c>.
/// </summary>
public partial class SnippetEditWindow : Window
{
    private bool _delete;

    private SnippetEditWindow(string name, string text, bool canDelete)
    {
        InitializeComponent();
        Title = canDelete ? "Edit snippet" : "New snippet";
        NameBox.Text = name ?? "";
        TextBox.Text = SnippetRules.NormalizeText(text);
        if (canDelete)
        {
            DeleteButton.Visibility = Visibility.Visible;
        }
        Loaded += (sender, e) => NameBox.Focus();
    }

    /// <summary>The trimmed name; empty means "use the text as the menu label".</summary>
    public string SnippetName => SnippetRules.TrimName(NameBox.Text);

    /// <summary>The text to paste.</summary>
    public string SnippetText => TextBox.Text;

    /// <summary>
    /// Shows the editor. Returns the result, or null when the user cancelled.
    /// </summary>
    internal static ThemedSnippetEditorResult Edit(Window owner, string name, string text, bool canDelete)
    {
        SnippetEditWindow window = new SnippetEditWindow(name, text, canDelete);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return null;
        }
        return new ThemedSnippetEditorResult
        {
            Name = window.SnippetName,
            Text = window.SnippetText,
            Delete = window._delete
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SnippetRules.IsAcceptable(TextBox.Text))
        {
            TextBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        _delete = true;
        DialogResult = true;
    }
}
