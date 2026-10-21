using System;
using System.Security;
using System.Windows;
using System.Windows.Controls;
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
                passwordBox.SelectAll();
            }
            PasswordBox passwordBox2 = txtPassword;
            if (passwordBox2 != null)
            {
                ((UIElement)passwordBox2).Focus();
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

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        try
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
            Controller.Connect();
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
