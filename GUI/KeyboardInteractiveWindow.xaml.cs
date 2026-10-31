using SmarTTY.Ssh;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Asks for what a server wants in a keyboard-interactive login besides the password: a verification code, a one-time
/// password, the new password of an expired account. The server decides how many prompts there are and whether the
/// answer is shown while typing, so the fields are made from the prompts.
/// </summary>
public partial class KeyboardInteractiveWindow : Window
{
    // A PasswordBox for a hidden answer, a TextBox for an echoed one.
    private readonly List<Control> _inputs = new List<Control>();

    private KeyboardInteractiveWindow(SSHConnectionSettings settings, string name, string instruction, SshSession.KeyboardInteractivePrompt[] prompts)
    {
        InitializeComponent();
        HostText.Text = settings.HostName;
        UserText.Text = settings.UserName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            Title = name.Trim();
        }
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            InstructionText.Text = instruction.Trim();
            InstructionText.Visibility = Visibility.Visible;
        }
        foreach (SshSession.KeyboardInteractivePrompt prompt in prompts)
        {
            TextBlock label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(prompt.Text) ? "Response:" : prompt.Text.Trim(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, _inputs.Count == 0 ? 0 : 8, 0, 2)
            };
            Control input = prompt.Echo ? (Control)new TextBox() : new PasswordBox();
            PromptsPanel.Children.Add(label);
            PromptsPanel.Children.Add(input);
            _inputs.Add(input);
        }
        Loaded += (object sender, RoutedEventArgs e) =>
        {
            if (_inputs.Count > 0)
            {
                _inputs[0].Focus();
            }
        };
    }

    /// <summary>
    /// Shows the prompts of the server; returns the answers in the order of the prompts, or null when the user cancelled.
    /// </summary>
    public static string[] Prompt(Window owner, SSHConnectionSettings settings, string name, string instruction, SshSession.KeyboardInteractivePrompt[] prompts)
    {
        KeyboardInteractiveWindow window = new KeyboardInteractiveWindow(settings, name, instruction, prompts);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return null;
        }
        string[] answers = new string[window._inputs.Count];
        for (int i = 0; i < answers.Length; i++)
        {
            answers[i] = window._inputs[i] is PasswordBox hidden ? hidden.Password : ((TextBox)window._inputs[i]).Text;
        }
        return answers;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
