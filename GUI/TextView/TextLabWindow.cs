using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VisualTTY;
using DrawingColor = System.Drawing.Color;

namespace SmarTTY.GUI.TextView;

/// <summary>
/// Tools > WPF text editor lab: the text view drawn with WPF (<see cref="WpfTextEditor"/>, stage 3D-2) on a file of this computer, before it takes
/// the place of the WinForms text view in the editor tab. Typing, the edit keys, undo, the clipboard, the mouse selection, scrolling, word wrap,
/// the line numbers and the tab size can be tried here; the text can be changed but is never saved.
/// </summary>
internal sealed class TextLabWindow : Window
{
    // A file larger than this is not read: the view keeps the whole text in memory, as the editor does.
    private const long MaxBytes = 16L * 1024 * 1024;

    private static readonly int[] TabSizes = { 2, 4, 8 };

    private readonly WpfTextEditor _editor = new WpfTextEditor();

    private readonly TextBlock _status = new TextBlock { Margin = new Thickness(6, 3, 6, 3) };

    private readonly CheckBox _readOnly = Option("Read-only", false);

    private string _fileName = "(new text)";

    public TextLabWindow(GlobalSettings settings)
    {
        Title = "WPF text editor lab";
        Width = 1000;
        Height = 650;
        SetResourceReference(StyleProperty, "SecondaryWindowStyle");
        WindowOwnership.Attach(this, WindowOwnership.ActiveWindow());

        GlobalSettings.EditorColors colors = settings.Editor.GetEffectiveColors(settings.IsLightTheme);
        _editor.SetColors(((DrawingColor)colors.BackColor).ToRgb(), ((DrawingColor)colors.TextColor).ToRgb(),
            ((DrawingColor)colors.LineNumberBackground).ToRgb(), ((DrawingColor)colors.LineNumberForeground).ToRgb());
        float zoom = settings.Editor.FontZoom > 0f ? settings.Editor.FontZoom : 1f;
        // The font of the editor tab: Courier New of 12 points, with the zoom of the editor.
        _editor.SetFont("Courier New", 12 * zoom);
        _editor.TabSize = settings.Editor.TabSize > 0 ? settings.Editor.TabSize : 4;
        _editor.InsertSpaces = settings.Editor.InsertSpaces;
        _editor.WordWrap = settings.Editor.WordWrap;
        _editor.ShowLineNumbers = settings.Editor.ShowLineNumbers;
        _editor.Document = new EditableTextDocument(new[] { "Open a file, or type here: the text is never saved.", "", "\tA tab,", "    four spaces,", "and a long line that goes on " + string.Concat(Enumerable.Repeat("and on ", 30)) + "to the end." }, TextDocument.LineEnding.CrLf);
        _editor.IsModified = false;

        Button open = new Button { Content = "Open file...", Padding = new Thickness(8, 2, 8, 2) };
        open.Click += (sender, e) => OpenFile();
        _readOnly.Click += (sender, e) => _editor.ReadOnly = _readOnly.IsChecked == true;
        CheckBox wrap = Option("Word wrap", _editor.WordWrap);
        wrap.Click += (sender, e) => _editor.WordWrap = wrap.IsChecked == true;
        CheckBox numbers = Option("Line numbers", _editor.ShowLineNumbers);
        numbers.Click += (sender, e) => _editor.ShowLineNumbers = numbers.IsChecked == true;
        CheckBox spaces = Option("Insert spaces", _editor.InsertSpaces);
        spaces.Click += (sender, e) => _editor.InsertSpaces = spaces.IsChecked == true;
        ComboBox tabSize = new ComboBox { MinWidth = 50, ToolTip = "Tab size" };
        foreach (int size in TabSizes)
        {
            tabSize.Items.Add(size);
        }
        if (Array.IndexOf(TabSizes, _editor.TabSize) < 0)
        {
            tabSize.Items.Add(_editor.TabSize);
        }
        tabSize.SelectedItem = _editor.TabSize;
        tabSize.SelectionChanged += (sender, e) =>
        {
            if (tabSize.SelectedItem is int size)
            {
                _editor.TabSize = size;
            }
        };

        StackPanel tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        tools.Children.Add(open);
        tools.Children.Add(_readOnly);
        tools.Children.Add(wrap);
        tools.Children.Add(numbers);
        tools.Children.Add(spaces);
        tools.Children.Add(new TextBlock { Text = "Tab size:", Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(tabSize);

        DockPanel panel = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        panel.Children.Add(tools);
        panel.Children.Add(_status);
        panel.Children.Add(_editor);
        Content = panel;

        _editor.StateChanged += (sender, e) => UpdateStatus();
        _editor.SaveRequested += (sender, e) => Note("The lab does not save: the text stays in memory.");
        Loaded += (sender, e) => _editor.Focus();
        UpdateStatus();
    }

    private static CheckBox Option(string text, bool value)
    {
        return new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    private void OpenFile()
    {
        string path = LocalFileDialogWindow.OpenFile(this, "Open a file in the WPF text editor lab", "All files (*.*)|*.*");
        if (path == null)
        {
            return;
        }
        try
        {
            FileInfo info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                MessageDialog.Show(path + " is larger than " + MaxBytes / (1024 * 1024) + " MB: the lab does not read it.", "WPF text editor lab", MessageDialogIcon.Warning, MessageDialogButtons.Ok, owner: this);
                return;
            }
            // UTF-8 unless the file starts with the mark of another Unicode encoding.
            string text = File.ReadAllText(path);
            EditableTextDocument document = new EditableTextDocument();
            document.SetText(text);
            _fileName = path;
            _editor.Document = document;
            _editor.IsModified = false;
            _editor.ReadOnly = _readOnly.IsChecked == true;
            UpdateStatus();
        }
        catch (Exception exception)
        {
            MessageDialog.Show(exception.Message, "WPF text editor lab", MessageDialogIcon.Error, MessageDialogButtons.Ok, owner: this);
        }
    }

    private void UpdateStatus()
    {
        TextDocument.TextPosition caret = _editor.CaretPosition;
        int column = _editor.Document.IndexToColumn(caret.Line, caret.Column, _editor.TabSize);
        string selection = _editor.HasSelection ? "selection " + _editor.SelectedText.Length.ToString(CultureInfo.InvariantCulture) + " characters" : "no selection";
        _status.Text = _fileName + "   |   Ln " + (caret.Line + 1).ToString(CultureInfo.InvariantCulture) + ", Col " + (column + 1).ToString(CultureInfo.InvariantCulture)
            + "   |   " + selection + "   |   " + _editor.Document.LineCount.ToString(CultureInfo.InvariantCulture) + " lines   |   " + (_editor.OverwriteMode ? "OVR" : "INS")
            + (_editor.ReadOnly ? "   |   read-only" : "") + (_editor.IsModified ? "   |   modified (not saved)" : "");
    }

    private void Note(string text)
    {
        UpdateStatus();
        _status.Text += "   |   " + text;
    }
}
