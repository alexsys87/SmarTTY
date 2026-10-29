using System;
using System.Linq;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using VisualTTY;
using VisualTTY.Backend;

namespace SmarTTY.GUI;

public partial class NewSSHConnectionWindow : Window, IComponentConnector, IStyleConnector
{
    private PasswordBox txtPassword;


    public NewConnectionController Controller { get; }

    public NewSSHConnectionWindow(SSHConnectionSettings settings, NewConnectionController.WindowCustomization customization)
    {
        InitializeComponent();
        this.DataContext = (Controller = new NewConnectionController(settings, customization, this.Dispatcher));
        Controller.CloseRequested += (bool r) =>
        {
            this.DialogResult = r;
        };
        Controller.ReportException += Controller_ReportException;
    }

    private void Controller_ReportException(Exception exception)
    {
        if (exception is SecurityException)
        {
            PasswordBox passwordBox = txtPassword;
            if (passwordBox != null)
            {
                // While the password is shown, its text box is the one that is visible.
                TextBox shown = passwordBox.IsVisible ? null : FindRevealTextBox(passwordBox);
                if (shown != null)
                {
                    shown.SelectAll();
                    shown.Focus();
                }
                else
                {
                    passwordBox.SelectAll();
                    passwordBox.Focus();
                }
            }
        }
        MessageBoxes.Exception(exception);
    }

    private void PasswordBox_Loaded(object sender, RoutedEventArgs e)
    {
        txtPassword = sender as PasswordBox;
        (sender as PasswordBox).Password = Controller.Password;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Controller.Password = (sender as PasswordBox).Password;
    }

    // Every secret on this form (password, passphrase, proxy "user:password@host:port") is a password box with an eye
    // button beside it. The button swaps the password box for a text box that holds the same text; the text box has no
    // binding: what is typed into it goes to the password box, whose PasswordChanged keeps the controller up to date.
    private static PasswordBox FindRevealPasswordBox(Panel panel)
    {
        return panel?.Children.OfType<PasswordBox>().FirstOrDefault();
    }

    private static TextBox FindRevealTextBox(PasswordBox passwordBox)
    {
        return (passwordBox.Parent as Panel)?.Children.OfType<TextBox>().FirstOrDefault();
    }

    private static void SetRevealed(object sender, bool revealed)
    {
        Panel panel = (sender as ToggleButton)?.Parent as Panel;
        PasswordBox passwordBox = FindRevealPasswordBox(panel);
        TextBox textBox = passwordBox == null ? null : FindRevealTextBox(passwordBox);
        if (textBox == null)
        {
            return;
        }
        if (revealed)
        {
            textBox.Text = passwordBox.Password;
            passwordBox.Visibility = Visibility.Collapsed;
            textBox.Visibility = Visibility.Visible;
            textBox.Focus();
            textBox.CaretIndex = textBox.Text.Length;
        }
        else
        {
            passwordBox.Visibility = Visibility.Visible;
            textBox.Visibility = Visibility.Collapsed;
            // The secret does not stay behind in a plain text box.
            textBox.Text = string.Empty;
            passwordBox.Focus();
        }
    }

    private void RevealButton_Checked(object sender, RoutedEventArgs e)
    {
        SetRevealed(sender, revealed: true);
    }

    private void RevealButton_Unchecked(object sender, RoutedEventArgs e)
    {
        SetRevealed(sender, revealed: false);
    }

    private void RevealText_TextChanged(object sender, TextChangedEventArgs e)
    {
        TextBox textBox = (TextBox)sender;
        if (textBox.Visibility != Visibility.Visible)
        {
            return;
        }
        PasswordBox passwordBox = FindRevealPasswordBox(textBox.Parent as Panel);
        if (passwordBox != null && passwordBox.Password != textBox.Text)
        {
            passwordBox.Password = textBox.Text;
        }
    }

    private bool _SyncingProxy;

    private void ProxyPasswordBox_Loaded(object sender, RoutedEventArgs e)
    {
        _SyncingProxy = true;
        try
        {
            pwdProxy.Password = Controller.Proxy ?? string.Empty;
        }
        finally
        {
            _SyncingProxy = false;
        }
    }

    private void ProxyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_SyncingProxy)
        {
            Controller.Proxy = pwdProxy.Password;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        Controller.BrowseForKeyFile();
    }

    private void PassphraseBox_Loaded(object sender, RoutedEventArgs e)
    {
        (sender as PasswordBox).Password = Controller.Passphrase;
    }

    private void PassphraseBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Controller.Passphrase = (sender as PasswordBox).Password;
    }

    // A text box that still has the focus has not handed its text to the controller yet.
    private static void CommitFocusedTextBox()
    {
        IInputElement focusedElement = Keyboard.FocusedElement;
        TextBox textBox = focusedElement as TextBox;
        if (textBox != null)
        {
            BindingExpression bindingExpression = ((FrameworkElement)textBox).GetBindingExpression(TextBox.TextProperty);
            if (bindingExpression != null)
            {
                ((BindingExpressionBase)bindingExpression).UpdateSource();
            }
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitFocusedTextBox();
            Controller.Connect();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void SaveOnly_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitFocusedTextBox();
            Controller.SaveWithoutConnecting();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Controller.Cancel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((int)e.Key == 13)
        {
            Controller.Cancel();
            ((RoutedEventArgs)e).Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = Controller?.HostName;
        if (text != null)
        {
            int index = text.IndexOf('@');
            if (index != -1)
            {
                Controller.UserName = text.Substring(0, index);
                Controller.HostName = text.Substring(index + 1);
                txtHostName.SelectionStart = txtHostName.Text.Length;
                txtHostName.SelectionLength = 0;
            }
        }
    }
}
