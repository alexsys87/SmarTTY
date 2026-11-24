using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using SmarTTY.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// A themed message box: unlike the WinForms <c>MessageBox</c> it takes its colours and its buttons from the
/// application theme, so it follows the light/dark switch. Built in code, like <see cref="GoToLineWindow"/>.
/// </summary>
internal sealed class MessageDialog : Window
{
    // A long message (a file list, an exception text) wraps at this width and scrolls past this height instead of
    // stretching the dialog over the screen.
    private const double MaxTextWidth = 440.0;

    private const double MaxTextHeight = 320.0;

    private readonly DialogButtons _Buttons;

    // The button Enter picks. Null means the first button, the one WinForms marks as default for the button set.
    private readonly MessageBoxResult? _DefaultResult;

    private readonly string _Message;

    private MessageBoxResult _Result;

    private MessageDialog(string text, string caption, DialogIcon icon, DialogButtons buttons, MessageBoxResult? defaultResult)
    {
        _Buttons = buttons;
        _DefaultResult = defaultResult;
        _Message = NormalizeLineEndings(text);
        // What dismissing the dialog (the close button of the title bar) reports: the same value the WinForms
        // MessageBox gives, so a closed prompt never counts as a "yes".
        _Result = DismissalResult(buttons);

        Title = caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");
        Content = BuildContent(_Message, icon, buttons);
    }

    /// <summary>
    /// Shows the dialog and returns what the user chose. Safe to call from a background thread: the dialog is then
    /// created on the UI thread, because a WPF window belongs to the thread that created it (a WinForms message box
    /// could be shown from any thread, a WPF one cannot).
    /// </summary>
    /// <param name="defaultResult">
    /// The button Enter picks, when it is not the first one. A prompt that defaults to the safe answer (for example
    /// "No" for a question about including passwords) passes it so that a stray Enter does not choose the risky one.
    /// </param>
    /// <param name="owner">
    /// The window to own the dialog. When it is null the dialog is owned by the active window.
    /// </param>
    /// <summary>
    /// The message box of <see cref="UserDialogs"/> (set by <c>App</c>): owned by the window behind the handle of the request, or
    /// by the window the user is working in; a box closed without a button is <see cref="DialogAnswer.Cancel"/>.
    /// </summary>
    internal static DialogAnswer Ask(MessageRequest request)
    {
        MessageBoxResult result = Show(
            request.Text,
            string.IsNullOrEmpty(request.Caption) ? "SmarTTY" : request.Caption,
            request.Icon,
            request.Buttons,
            defaultResult: null,
            owner: WindowOwnership.FromHandle(request.OwnerHandle));
        switch (result)
        {
            case MessageBoxResult.Yes:
                return DialogAnswer.Yes;
            case MessageBoxResult.No:
                return DialogAnswer.No;
            case MessageBoxResult.OK:
                return DialogAnswer.Ok;
            default:
                return DialogAnswer.Cancel;
        }
    }

    public static MessageBoxResult Show(string text, string caption, DialogIcon icon, DialogButtons buttons, MessageBoxResult? defaultResult = null, Window owner = null)
    {
        Application application = Application.Current;
        if (application != null && !application.Dispatcher.CheckAccess())
        {
            return application.Dispatcher.Invoke(() => ShowOnUiThread(text, caption, icon, buttons, defaultResult, owner));
        }
        return ShowOnUiThread(text, caption, icon, buttons, defaultResult, owner);
    }

    private static MessageBoxResult ShowOnUiThread(string text, string caption, DialogIcon icon, DialogButtons buttons, MessageBoxResult? defaultResult, Window owner)
    {
        MessageDialog dialog = new MessageDialog(text, caption, icon, buttons, defaultResult);
        // Owned by the window the user is working in, so the prompt stays above it and is centered on it.
        WindowOwnership.Attach(dialog, owner ?? WindowOwnership.ActiveWindow());
        // A dialog that has no owner (no window is visible) must at least be reachable from the taskbar, and every dialog
        // asks for the focus: the window flashes when another program is in front instead of waiting unseen.
        dialog.ShowInTaskbar = dialog.Owner == null;
        dialog.Loaded += (sender, e) => dialog.Activate();
        dialog.ShowDialog();
        return dialog._Result;
    }

    private static MessageBoxResult DismissalResult(DialogButtons buttons)
    {
        return (buttons == DialogButtons.Ok) ? MessageBoxResult.OK
            : (buttons == DialogButtons.YesNo) ? MessageBoxResult.No
            : MessageBoxResult.Cancel;
    }

    // The dialog is closed with Close() and the chosen value is read from _Result, so the outcome does not depend on
    // how WPF treats IsCancel. Escape dismisses it, and Ctrl+C copies the text, as a message box does.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _Result = DismissalResult(_Buttons);
            e.Handled = true;
            Close();
            return;
        }
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyMessage();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void CopyMessage()
    {
        try
        {
            Clipboard.SetText(_Message);
        }
        catch (Exception)
        {
            // The clipboard is a shared resource; another process may hold it. Losing the copy is harmless.
        }
    }

    // A TextBlock lays a "\r\n" out as a single line break, exactly like the message box does, but normalizing keeps
    // the layout independent of the line endings the caller happened to use.
    private static string NormalizeLineEndings(string text)
    {
        return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private Grid BuildContent(string text, DialogIcon icon, DialogButtons buttons)
    {
        Grid root = new Grid
        {
            Margin = new Thickness(14, 14, 14, 12)
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Image glyph = new Image
        {
            Source = LoadIcon(icon),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 2, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(glyph, 0);
        Grid.SetColumn(glyph, 0);
        root.Children.Add(glyph);

        TextBlock message = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaxTextWidth
        };
        ScrollViewer scroller = new ScrollViewer
        {
            Content = message,
            MaxHeight = MaxTextHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(scroller, 0);
        Grid.SetColumn(scroller, 1);
        root.Children.Add(scroller);

        StackPanel bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        switch (buttons)
        {
            case DialogButtons.Ok:
                bar.Children.Add(CreateButton(bar, "OK", MessageBoxResult.OK));
                break;
            case DialogButtons.OkCancel:
                bar.Children.Add(CreateButton(bar, "OK", MessageBoxResult.OK));
                bar.Children.Add(CreateButton(bar, "Cancel", MessageBoxResult.Cancel));
                break;
            case DialogButtons.YesNo:
                bar.Children.Add(CreateButton(bar, "Yes", MessageBoxResult.Yes));
                bar.Children.Add(CreateButton(bar, "No", MessageBoxResult.No));
                break;
            default:
                bar.Children.Add(CreateButton(bar, "Yes", MessageBoxResult.Yes));
                bar.Children.Add(CreateButton(bar, "No", MessageBoxResult.No));
                bar.Children.Add(CreateButton(bar, "Cancel", MessageBoxResult.Cancel));
                break;
        }
        Grid.SetRow(bar, 1);
        Grid.SetColumn(bar, 0);
        Grid.SetColumnSpan(bar, 2);
        root.Children.Add(bar);
        return root;
    }

    private Button CreateButton(Panel bar, string text, MessageBoxResult result)
    {
        // Enter picks the caller's default button, or the first one (OK, or Yes) when it named none.
        bool isDefault = _DefaultResult.HasValue ? _DefaultResult.Value == result : bar.Children.Count == 0;
        Button button = new Button
        {
            Content = text,
            MinWidth = 75,
            IsDefault = isDefault,
            Margin = new Thickness((bar.Children.Count == 0) ? 0 : 8, 0, 0, 0)
        };
        // The same automation ids the other dialogs use, so UI tests can find the buttons by name.
        AutomationProperties.SetAutomationId(button, result.ToString());
        button.Click += (object sender, RoutedEventArgs e) =>
        {
            _Result = result;
            Close();
        };
        return button;
    }

    private static ImageSource LoadIcon(DialogIcon icon)
    {
        string name = (icon == DialogIcon.Error) ? "err16.png"
            : (icon == DialogIcon.Warning) ? "editor/message-warning.png"
            : (icon == DialogIcon.Question) ? "question16.png"
            : "info16.png";
        return IconImages.Get(name);
    }
}
