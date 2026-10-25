using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI;

/// <summary>
/// Editor for a user command list of the line-input mode: one command per line, lines starting with '#' are
/// comments. Enter belongs to the text box here, so OK is reached with the mouse or Ctrl+Enter.
/// </summary>
public partial class DeviceCommandListWindow : Window
{
    private DeviceCommandListWindow(string title, string description, IEnumerable<string> commands)
    {
        InitializeComponent();
        Title = title;
        DescriptionText.Text = description;
        // The box works on CRLF, as the WinForms editor did; Format() returns LF-separated lines.
        CommandsBox.Text = CommandListFile.Format(commands).Replace("\r\n", "\n").Replace("\n", "\r\n");
        Loaded += (sender, e) => CommandsBox.Focus();
    }

    /// <summary>The commands as edited (comments, blank lines and duplicates removed).</summary>
    public List<string> Commands => CommandListFile.Parse(CommandsBox.Text);

    /// <summary>Shows the editor. Returns the edited commands, or null when it was cancelled.</summary>
    public static List<string> Edit(Window owner, string title, string description, IEnumerable<string> commands)
    {
        DeviceCommandListWindow window = new DeviceCommandListWindow(title, description, commands);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        return window.ShowDialog() == true ? window.Commands : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Enter belongs to the text box (one command per line), so Ctrl+Enter accepts instead.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            DialogResult = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
