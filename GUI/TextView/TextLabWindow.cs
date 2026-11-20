using SmarTTY.CodeEditing;
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

    private readonly ComboBox _syntax = new ComboBox { MinWidth = 150, ToolTip = "Syntax" };

    private readonly GlobalSettings _settings;

    private string _fileName = "(new text)";

    public TextLabWindow(GlobalSettings settings)
    {
        _settings = settings;
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
        // As RemoteFileEditor.RefreshMessageColors: the band in the colour of the margin, its frame in the colour of the line numbers.
        _editor.SetMessageColors(((DrawingColor)colors.LineNumberBackground).ToRgb(), ((DrawingColor)colors.ErrorColor).ToRgb(),
            ((DrawingColor)colors.WarningColor).ToRgb(), ((DrawingColor)colors.LineNumberForeground).ToRgb());
        _editor.TabSize = settings.Editor.TabSize > 0 ? settings.Editor.TabSize : 4;
        _editor.InsertSpaces = settings.Editor.InsertSpaces;
        _editor.WordWrap = settings.Editor.WordWrap;
        _editor.ShowLineNumbers = settings.Editor.ShowLineNumbers;
        _editor.Document = new EditableTextDocument(new[] { "/* Open a file, or type here: the text is never saved. */", "#include <stdio.h>", "int main(void) { return printf(\"%d\\n\", 42) > 0; } // C", "\tA tab,", "    four spaces,", "and a long line that goes on " + string.Concat(Enumerable.Repeat("and on ", 30)) + "to the end." }, TextDocument.LineEnding.CrLf);
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
        foreach (SyntaxHighlighting.Language language in SyntaxHighlighting.SelectableLanguages)
        {
            _syntax.Items.Add(new ComboBoxItem { Content = SyntaxHighlighting.GetDisplayName(language), Tag = language });
        }
        _syntax.SelectionChanged += (sender, e) =>
        {
            if (_syntax.SelectedItem is ComboBoxItem item && item.Tag is SyntaxHighlighting.Language language)
            {
                ApplySyntax(language);
            }
        };
        tools.Children.Add(new TextBlock { Text = "Syntax:", Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(_syntax);
        // A check of the editor tab puts its problems under the lines; here they are put at the caret by hand.
        tools.Children.Add(MessageButton("Error here", () => AddMessageAtCaret(InlineMessageProvider.MessageSeverity.Error)));
        tools.Children.Add(MessageButton("Warning here", () => AddMessageAtCaret(InlineMessageProvider.MessageSeverity.Warning)));
        tools.Children.Add(MessageButton("Clear messages", () =>
        {
            _editor.ClearMessages();
            _editor.Focus();
        }));

        DockPanel panel = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        panel.Children.Add(tools);
        panel.Children.Add(_status);
        panel.Children.Add(_editor);
        Content = panel;

        ChooseSyntax(SyntaxHighlighting.Language.CLike);
        _editor.StateChanged += (sender, e) => UpdateStatus();
        _editor.SaveRequested += (sender, e) => Note("The lab does not save: the text stays in memory.");
        Loaded += (sender, e) => _editor.Focus();
        UpdateStatus();
    }

    private static Button MessageButton(string text, Action click)
    {
        Button button = new Button { Content = text, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(10, 0, 0, 0) };
        button.Click += (sender, e) => click();
        return button;
    }

    private void AddMessageAtCaret(InlineMessageProvider.MessageSeverity severity)
    {
        TextDocument.TextPosition caret = _editor.CaretPosition;
        string kind = severity == InlineMessageProvider.MessageSeverity.Error ? "An error" : "A warning";
        // A message without a column goes in the middle of its row: every other one is put under the caret.
        int column = (_editor.MessageCount % 2 == 0) ? caret.Column + 1 : 0;
        _editor.AddMessage(caret.Line + 1, severity, kind + " under line " + (caret.Line + 1).ToString(CultureInfo.InvariantCulture) + (column == 0 ? ", without a column" : ", column " + column.ToString(CultureInfo.InvariantCulture)), column);
        _editor.Focus();
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
            // As the editor tab: the syntax of the name or of the first line when the highlighting is on in the settings.
            ChooseSyntax(_settings.Editor.SyntaxHighlighting ? SyntaxHighlighting.DetectLanguage(path, document.LineCount > 0 ? document.GetLineText(0) : null) : SyntaxHighlighting.Language.Plain);
            _editor.ReadOnly = _readOnly.IsChecked == true;
            UpdateStatus();
        }
        catch (Exception exception)
        {
            MessageDialog.Show(exception.Message, "WPF text editor lab", MessageDialogIcon.Error, MessageDialogButtons.Ok, owner: this);
        }
    }

    private void ChooseSyntax(SyntaxHighlighting.Language language)
    {
        foreach (ComboBoxItem item in _syntax.Items)
        {
            if (item.Tag is SyntaxHighlighting.Language itemLanguage && itemLanguage == language)
            {
                if (ReferenceEquals(_syntax.SelectedItem, item))
                {
                    ApplySyntax(language);
                }
                _syntax.SelectedItem = item;
                return;
            }
        }
        ApplySyntax(language);
    }

    // The tokenizer of the language and the colours of the token types of the settings, in the order MainWindowController gives them to the editor.
    private void ApplySyntax(SyntaxHighlighting.Language language)
    {
        TokenCache.LineTokenizer tokenizer = SyntaxHighlighting.GetTokenizer(language);
        if (tokenizer == null)
        {
            _editor.SetSyntax(null, null);
            return;
        }
        GlobalSettings.EditorColors colors = _settings.Editor.GetEffectiveColors(_settings.IsLightTheme);
        _editor.SetSyntax(tokenizer, new[]
        {
            ((DrawingColor)colors.TextColor).ToRgb(),
            ((DrawingColor)colors.KeywordColor).ToRgb(),
            ((DrawingColor)colors.StringColor).ToRgb(),
            ((DrawingColor)colors.CommentColor).ToRgb(),
            ((DrawingColor)colors.NumberColor).ToRgb(),
            ((DrawingColor)colors.VariableColor).ToRgb(),
            ((DrawingColor)colors.SectionColor).ToRgb(),
            ((DrawingColor)colors.PreprocessorColor).ToRgb()
        });
    }

    private void UpdateStatus()
    {
        TextDocument.TextPosition caret = _editor.CaretPosition;
        int column = _editor.Document.IndexToColumn(caret.Line, caret.Column, _editor.TabSize);
        string selection = _editor.HasSelection ? "selection " + _editor.SelectedText.Length.ToString(CultureInfo.InvariantCulture) + " characters" : "no selection";
        _status.Text = _fileName + "   |   Ln " + (caret.Line + 1).ToString(CultureInfo.InvariantCulture) + ", Col " + (column + 1).ToString(CultureInfo.InvariantCulture)
            + "   |   " + selection + "   |   " + _editor.Document.LineCount.ToString(CultureInfo.InvariantCulture) + " lines   |   " + (_editor.OverwriteMode ? "OVR" : "INS")
            + (_editor.MessageCount > 0 ? "   |   " + _editor.MessageCount.ToString(CultureInfo.InvariantCulture) + " message(s)" : "")
            + (_editor.ReadOnly ? "   |   read-only" : "") + (_editor.IsModified ? "   |   modified (not saved)" : "");
    }

    private void Note(string text)
    {
        UpdateStatus();
        _status.Text += "   |   " + text;
    }
}
