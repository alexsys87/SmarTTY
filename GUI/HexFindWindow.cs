using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmarTTY.GUI;

/// <summary>
/// Finds bytes in the hex view of the file editor. A modeless window: the found bytes are selected in the editor, and
/// F3 there (or here) repeats the search. "Hex bytes" reads the query as "48 65 6C" or "48656c"; otherwise the text is
/// searched as UTF-8 (and "ignore case" folds ASCII letters).
/// </summary>
internal sealed class HexFindWindow : Window
{
    private readonly HexEditorControl _editor;

    private readonly TextBox _query;

    private readonly CheckBox _hexBytes;

    private readonly CheckBox _ignoreCase;

    private readonly CheckBox _wrap;

    private readonly TextBlock _result;

    public HexFindWindow(HexEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Title = "Find in Hex";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");
        WindowOwnership.Attach(this, WindowOwnership.ActiveWindow());

        StackPanel panel = new StackPanel
        {
            Margin = new Thickness(10)
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Bytes to find (hex: 48 65 6C, or text):",
            Margin = new Thickness(0, 0, 0, 5)
        });
        string last = _editor.LastSearchText;
        _query = new TextBox
        {
            Text = last,
            MinWidth = 320
        };
        // The last search is kept as hex bytes, so that F3 repeats exactly what was found before, text or bytes.
        _hexBytes = new CheckBox
        {
            Content = "Hex bytes",
            IsChecked = !string.IsNullOrEmpty(last),
            Margin = new Thickness(0, 5, 0, 0),
            ToolTip = "Read the query as hexadecimal bytes, for example 48 65 6C or 0x48, 0x65"
        };
        _ignoreCase = new CheckBox
        {
            Content = "Ignore case (ASCII letters)",
            IsChecked = false,
            Margin = new Thickness(0, 3, 0, 0)
        };
        _wrap = new CheckBox
        {
            Content = "Wrap around",
            IsChecked = true,
            Margin = new Thickness(0, 3, 0, 0)
        };
        _result = new TextBlock
        {
            Text = "",
            Margin = new Thickness(0, 6, 0, 0),
            MaxWidth = 360,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_query);
        panel.Children.Add(_hexBytes);
        panel.Children.Add(_ignoreCase);
        panel.Children.Add(_wrap);
        panel.Children.Add(_result);

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Button previous = new Button
        {
            Content = "Find Previous",
            MinWidth = 90,
            Margin = new Thickness(0, 0, 5, 0)
        };
        previous.Click += (sender, e) => Find(forward: false);
        Button next = new Button
        {
            Content = "Find Next",
            IsDefault = true,
            MinWidth = 90,
            Margin = new Thickness(0, 0, 5, 0)
        };
        next.Click += (sender, e) => Find(forward: true);
        Button close = new Button
        {
            Content = "Close",
            IsCancel = true,
            MinWidth = 75
        };
        close.Click += (sender, e) => Close();
        buttons.Children.Add(previous);
        buttons.Children.Add(next);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (object sender, RoutedEventArgs e) =>
        {
            _query.Focus();
            _query.SelectAll();
        };
        Closed += (object sender, EventArgs e) =>
        {
            if (!_editor.IsDisposed)
            {
                _editor.Focus();
            }
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F3 && e.KeyboardDevice.Modifiers != ModifierKeys.Control)
        {
            Find(forward: (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift);
            e.Handled = true;
        }
    }

    private void Find(bool forward)
    {
        string query = _query.Text ?? "";
        byte[] pattern;
        if (_hexBytes.IsChecked == true)
        {
            if (!HexDocument.TryParseHexBytes(query, out pattern, out string error))
            {
                _result.Text = error;
                _query.Focus();
                return;
            }
        }
        else
        {
            if (query.Length == 0)
            {
                _result.Text = "Enter the text to find.";
                _query.Focus();
                return;
            }
            pattern = Encoding.UTF8.GetBytes(query);
        }
        bool wrap = _wrap.IsChecked == true;
        bool ignoreCase = _ignoreCase.IsChecked == true;
        if (_editor.Search(pattern, forward, ignoreCase, wrap))
        {
            _result.Text = "Found " + EditorStatusText.HexSelection(_editor.SelectionLength)
                + " at offset 0x" + _editor.SelectionStart.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ".";
        }
        else
        {
            _result.Text = "Not found.";
            SystemSounds.Beep.Play();
        }
    }
}
