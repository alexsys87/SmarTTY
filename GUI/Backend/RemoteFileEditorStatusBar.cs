using SmarTTY.CodeEditing;
using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// The status bar below the remote file editor: the file, whether it is saved, the caret, the selection, and the
/// encoding and the line endings, which are menus. WPF scales it for high-DPI monitors by itself, as the tool bar above
/// the editor. It reads the editor when the panel reports a change (see <see cref="RemoteFileEditorPanel.StateChanged"/>).
/// </summary>
internal sealed class RemoteFileEditorStatusBar : StatusBar
{
    private readonly RemoteFileEditor _editor;

    private readonly TextBlock _path = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly TextBlock _state = new TextBlock();

    private readonly TextBlock _position = new TextBlock { ToolTip = "Line and column of the caret" };

    private readonly TextBlock _selection = new TextBlock { ToolTip = "The selected text" };

    private readonly StatusBarItem _selectionItem;

    private readonly Button _encodingButton;

    private readonly Button _lineEndingButton;

    private readonly Button _syntaxButton;

    public RemoteFileEditorStatusBar(RemoteFileEditor editor, RemoteFileEditorPanel panel)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        if (panel == null)
        {
            throw new ArgumentNullException(nameof(panel));
        }
        _encodingButton = CreateMenuButton(ShowEncodingMenu);
        _lineEndingButton = CreateMenuButton(ShowLineEndingMenu);
        _syntaxButton = CreateMenuButton(ShowSyntaxMenu);
        _selectionItem = CreateItem(_selection, Dock.Right);

        // A DockPanel fills the last item and stacks the others from the right: the first one added is the rightmost.
        Items.Add(CreateItem(_lineEndingButton, Dock.Right));
        Items.Add(CreateItem(_encodingButton, Dock.Right));
        Items.Add(CreateItem(_syntaxButton, Dock.Right));
        Items.Add(_selectionItem);
        Items.Add(CreateItem(_position, Dock.Right));
        Items.Add(CreateItem(_state, Dock.Right));
        Items.Add(new StatusBarItem { Content = _path });

        panel.StateChanged += (sender, e) => Update();
        editor.CurrentLanguageChanged += (sender, e) => Update();
        Update();
    }

    private static StatusBarItem CreateItem(UIElement content, Dock dock)
    {
        StatusBarItem item = new StatusBarItem
        {
            Content = content,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(item, dock);
        return item;
    }

    private static Button CreateMenuButton(Action<Button> showMenu)
    {
        Button button = new Button { Padding = new Thickness(6, 0, 6, 0) };
        // The same flat look as the buttons of the tool bar.
        button.SetResourceReference(StyleProperty, ToolBar.ButtonStyleKey);
        button.Click += (sender, e) => showMenu(button);
        return button;
    }

    private static void SetWarning(Control control, bool warning)
    {
        if (warning)
        {
            control.Foreground = Brushes.DarkOrange;
        }
        else
        {
            control.ClearValue(ForegroundProperty);
        }
    }

    private void Update()
    {
        bool hex = _editor.IsHexMode;
        bool readOnly = hex ? _editor.HexEditor.ReadOnly : _editor.ReadOnly;
        bool loading = readOnly && !_editor.IsReadOnlyMode;
        _state.Text = EditorStatusText.State(loading, _editor.IsReadOnlyMode, _editor.IsSaving, _editor.IsModified, _editor.IsNewFile);
        if (_editor.IsModified || _editor.IsSaving)
        {
            _state.Foreground = Brushes.DarkOrange;
        }
        else
        {
            _state.ClearValue(TextBlock.ForegroundProperty);
        }

        _path.Text = _editor.IsPreview ? (_editor.RemotePath + " (the beginning of the file)") : _editor.RemotePath;
        _path.ToolTip = _editor.RemotePath;

        if (hex)
        {
            HexEditorControl hexEditor = _editor.HexEditor;
            _position.Text = EditorStatusText.HexPosition(hexEditor.CaretOffset, hexEditor.Length, hexEditor.InsertMode);
            _position.ToolTip = "Byte offset of the caret, the size of the file, and the typing mode (Insert toggles it)";
            int selectedBytes = hexEditor.SelectionLength;
            _selectionItem.Visibility = (selectedBytes > 0) ? Visibility.Visible : Visibility.Collapsed;
            if (selectedBytes > 0)
            {
                _selection.Text = EditorStatusText.HexSelection(selectedBytes);
                _selection.ToolTip = "The selected bytes";
            }
        }
        else
        {
            TextDocument.TextPosition caret = _editor.CaretPosition;
            _position.Text = EditorStatusText.Position(caret.Line, caret.Column);
            _position.ToolTip = "Line and column of the caret";

            int selectedLength = GetSelectionLength(out int selectedLines);
            _selectionItem.Visibility = (selectedLength > 0) ? Visibility.Visible : Visibility.Collapsed;
            if (selectedLength > 0)
            {
                _selection.Text = EditorStatusText.Selection(selectedLength, selectedLines);
                _selection.ToolTip = "The selected text";
            }
        }

        // A byte order mark breaks scripts and CRLF breaks shell scripts and OpenWrt configuration files: make them stand out.
        TextFileEncoding encoding = _editor.FileEncoding;
        _encodingButton.Content = encoding.DisplayName;
        bool bom = encoding == TextFileEncodings.Utf8Bom;
        SetWarning(_encodingButton, bom && !hex);
        _encodingButton.ToolTip = hex
            ? "The file is shown as bytes: the encoding applies to the text view"
            : (bom
                ? "UTF-8 with a byte order mark: it breaks a script that starts with #!. Choose UTF-8 in this menu to save without it."
                : "Encoding of the file: choose how it is saved, or read it again in another encoding");
        _encodingButton.IsEnabled = !loading && !hex;

        SyntaxHighlighting.Language language = _editor.CurrentLanguage;
        bool automatic = _editor.SyntaxOverride == null;
        _syntaxButton.Content = SyntaxHighlighting.GetDisplayName(language);
        _syntaxButton.ToolTip = hex
            ? "The file is shown as bytes: syntax highlighting applies to the text view"
            : (automatic
                ? "Syntax highlighting, detected from the file name and the #! line: click to choose another"
                : "Syntax highlighting, chosen by hand: click to change it or to detect it automatically");
        _syntaxButton.FontStyle = automatic ? FontStyles.Normal : FontStyles.Italic;
        _syntaxButton.IsEnabled = !hex;

        LineEndingKind lineEndings = _editor.LineEndings;
        _lineEndingButton.Content = LineEndingDetector.GetName(lineEndings);
        bool windowsEndings = lineEndings == LineEndingKind.CrLf || lineEndings == LineEndingKind.Mixed;
        SetWarning(_lineEndingButton, windowsEndings && !hex);
        _lineEndingButton.ToolTip = hex
            ? "The file is shown as bytes: line endings apply to the text view"
            : (windowsEndings
                ? "CRLF line endings break shell scripts and OpenWrt configuration files. Choose LF in this menu to convert them."
                : "Line endings of the file: choose how it is saved");
        _lineEndingButton.IsEnabled = !readOnly && !hex;
    }

    /// <summary>The length of the selection in characters (line breaks included) and the number of lines it touches.</summary>
    private int GetSelectionLength(out int lines)
    {
        lines = 0;
        TextDocument.TextPosition first = _editor.SelectionStart;
        TextDocument.TextPosition second = _editor.SelectionEnd;
        if (!_editor.HasSelection || !first.IsValid || !second.IsValid || first == second)
        {
            return 0;
        }
        TextDocument.TextPosition start = (first < second) ? first : second;
        TextDocument.TextPosition end = (first < second) ? second : first;
        lines = end.Line - start.Line + 1;
        if (start.Line == end.Line)
        {
            return end.Column - start.Column;
        }
        EditableTextDocument document = _editor.EditableDocument;
        int length = document.GetLineText(start.Line).Length - start.Column + document.GetEffectiveLineTerminator(start.Line).Length;
        for (int line = start.Line + 1; line < end.Line; line++)
        {
            length += document.GetLineText(line).Length + document.GetEffectiveLineTerminator(line).Length;
        }
        return length + end.Column;
    }

    private bool ConfirmDiscardChanges(string question)
    {
        return !_editor.IsModified || MessageBoxes.AskWarn(question);
    }

    private static MenuItem CreateHeader(string text)
    {
        return new MenuItem { Header = text, IsEnabled = false, FontWeight = FontWeights.SemiBold };
    }

    private static void OpenMenu(Button button, ContextMenu menu)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void ShowEncodingMenu(Button button)
    {
        ContextMenu menu = new ContextMenu();
        menu.Items.Add(CreateHeader("Save with encoding"));
        foreach (TextFileEncoding encoding in TextFileEncodings.All)
        {
            TextFileEncoding chosen = encoding;
            MenuItem item = new MenuItem { Header = encoding.DisplayName, IsChecked = encoding == _editor.FileEncoding, IsEnabled = !_editor.ReadOnly };
            item.Click += (sender, e) =>
            {
                _editor.SetSaveEncoding(chosen);
                _editor.FocusActiveView();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateHeader("Reopen with encoding"));
        foreach (TextFileEncoding encoding in TextFileEncodings.All)
        {
            TextFileEncoding chosen = encoding;
            MenuItem item = new MenuItem { Header = encoding.DisplayName, IsEnabled = _editor.CanReopenWithEncoding && !_editor.IsSaving };
            item.Click += (sender, e) =>
            {
                if (ConfirmDiscardChanges("Read " + _editor.RemotePath + " again as " + chosen.DisplayName + "? Your changes will be lost."))
                {
                    _editor.ReopenWithEncoding(chosen);
                }
                _editor.FocusActiveView();
            };
            menu.Items.Add(item);
        }
        OpenMenu(button, menu);
    }

    private void ShowSyntaxMenu(Button button)
    {
        ContextMenu menu = new ContextMenu();
        menu.Items.Add(CreateHeader("Syntax highlighting"));
        SyntaxHighlighting.Language detected = SyntaxHighlighting.DetectLanguage(_editor.RemotePath, _editor.EditableDocument?.GetLineText(0));
        MenuItem automatic = new MenuItem
        {
            Header = "Detect automatically (" + SyntaxHighlighting.GetDisplayName(detected) + ")",
            IsChecked = _editor.SyntaxOverride == null
        };
        automatic.Click += (sender, e) =>
        {
            _editor.SetSyntax(null);
            _editor.FocusActiveView();
        };
        menu.Items.Add(automatic);
        menu.Items.Add(new Separator());
        foreach (SyntaxHighlighting.Language language in SyntaxHighlighting.SelectableLanguages)
        {
            SyntaxHighlighting.Language chosen = language;
            MenuItem item = new MenuItem
            {
                Header = SyntaxHighlighting.GetDisplayName(language),
                IsChecked = _editor.SyntaxOverride == language
            };
            item.Click += (sender, e) =>
            {
                _editor.SetSyntax(chosen);
                _editor.FocusActiveView();
            };
            menu.Items.Add(item);
        }
        OpenMenu(button, menu);
    }

    private void ShowLineEndingMenu(Button button)
    {
        ContextMenu menu = new ContextMenu();
        menu.Items.Add(CreateHeader("Save with line endings"));
        AddLineEndingItem(menu, "LF (Unix, OpenWrt)", TextDocument.LineEnding.Lf, LineEndingKind.Lf);
        AddLineEndingItem(menu, "CRLF (Windows)", TextDocument.LineEnding.CrLf, LineEndingKind.CrLf);
        AddLineEndingItem(menu, "CR (old Mac)", TextDocument.LineEnding.Cr, LineEndingKind.Cr);
        OpenMenu(button, menu);
    }

    private void AddLineEndingItem(ContextMenu menu, string text, TextDocument.LineEnding lineEnding, LineEndingKind kind)
    {
        MenuItem item = new MenuItem { Header = text, IsChecked = _editor.LineEndings == kind };
        item.Click += (sender, e) =>
        {
            _editor.SetLineEnding(lineEnding);
            _editor.FocusActiveView();
        };
        menu.Items.Add(item);
    }
}
