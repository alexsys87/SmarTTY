using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// The tab of a file on the server (stage 3E-4: on WPF): the WPF text view and the WPF hex view of a <see cref="RemoteFileEditor"/>, one of them
/// shown, with the tool bar and the status bar of the editor and their context menus, all running the same <see cref="RemoteFileEditorCommands"/>.
/// A Markdown file can also be shown rendered beside its text (<see cref="TextView.MarkdownPreview"/>, the Markdown Preview command): the preview
/// follows the text a moment after it changes and brings the block of the caret into view.
/// </summary>
public class EditorTabContext : ITabContext, IDisposable, INotifyPropertyChanged, ICloseableElement, IHexViewPlace
{
    private SessionWrapper _Session;

    public readonly RemoteFileEditor Editor;

    private string _Title;

    // The find window of the hex view (modeless).
    private HexFindWindow _hexFindWindow;

    // The two views of the file, one shown in the place of the other.
    private readonly TextView.WpfTextEditor _textView;

    private readonly HexView.WpfHexEditor _wpfHexView;

    // The Markdown preview at the right of the text, the splitter between them and their columns.
    private readonly TextView.MarkdownPreview _preview;

    private readonly GridSplitter _splitter;

    private readonly ColumnDefinition _splitterColumn;

    private readonly ColumnDefinition _previewColumn;

    // The preview is drawn again this long after the last change of the text.
    private readonly UiTimer _previewTimer = new UiTimer { Interval = 300 };

    private GridLength _previewWidth = new GridLength(1, GridUnitType.Star);

    private bool _previewShown;

    // The heading a link from another file asked for, shown once the text has been loaded.
    private string _pendingAnchor;

    private bool _textLoaded;

    public readonly string FilePath;

    public FrameworkElement Control { get; }

    public FrameworkElement ToolBar { get; }

    public FrameworkElement StatusBar { get; }

    public FileExplorer FileExplorer => null;

    /// <summary>The text view of the file.</summary>
    internal TextView.WpfTextEditor TextView => _textView;

    /// <summary>The commands of the editor and their state (the tool bar, the context menus and the keys run them).</summary>
    internal RemoteFileEditorCommands Commands { get; }

    public string Title
    {
        get
        {
            return _Title;
        }
        set
        {
            _Title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Title"));
        }
    }

    public IconType Icon => IconType.Editor;

    public TextSearcher Searcher { get; }

    // The two views draw their own scroll bars.
    public FrameworkElement VerticalScrollBar => null;

    public FrameworkElement HorizontalScrollBar => null;

    public string ToolTip => FilePath;

    public bool IsNewSessionShortcut => false;

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>A link of the Markdown preview to another file was clicked: its full path and the anchor after # (or null).</summary>
    public event Action<string, string> FileLinkClicked;

    /// <summary>The Markdown preview is shown beside the text.</summary>
    internal bool IsMarkdownPreviewShown => _previewShown;

    public EditorTabContext(SessionWrapper sessionWrapper, RemoteFileEditor editor, string filePath)
    {
        Editor = editor;
        _Session = sessionWrapper;
        _textView = editor.TextView as TextView.WpfTextEditor ?? throw new ArgumentException("The tab shows the WPF text view of the editor.", nameof(editor));
        _wpfHexView = editor.HexEditor as HexView.WpfHexEditor ?? throw new ArgumentException("The tab shows the WPF hex view of the editor.", nameof(editor));
        Commands = new RemoteFileEditorCommands(editor, PostRefresh);
        // What the commands depend on in the text view: the selection, the text and its undo history (the commands follow the rest).
        _textView.StateChanged += (object sender, EventArgs e) => Commands.ScheduleRefresh();
        _textView.RangeChanged += (start, oldEnd, newEnd) => Commands.ScheduleRefresh();
        _textView.KeyUp += (object sender, System.Windows.Input.KeyEventArgs e) => Commands.ScheduleRefresh();
        _textView.ContextMenu = new TextView.TextViewContextMenu(Commands);
        // The hex view lies over the place of the text: one of the two is shown.
        _wpfHexView.Visibility = Visibility.Collapsed;
        _wpfHexView.ContextMenu = new HexView.HexViewContextMenu(Commands, _wpfHexView, ShowHexDialog, () =>
        {
            Editor.FocusActiveView();
            Commands.RefreshState();
        });
        // The text (or the hex view) at the left, the Markdown preview at the right, hidden with its splitter until it is switched on.
        _preview = new TextView.MarkdownPreview
        {
            DocumentPath = filePath,
            Visibility = Visibility.Collapsed
        };
        _preview.FileLinkClicked += (path, anchor) => FileLinkClicked?.Invoke(path, anchor);
        _preview.PreviewKeyDown += (object sender, KeyEventArgs e) =>
        {
            if (IsMarkdownPreviewKey(e))
            {
                e.Handled = TryToggleMarkdownPreview();
            }
        };
        _splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Focusable = false,
            Visibility = Visibility.Collapsed
        };
        _splitter.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Theme.Divider");
        Grid place = new Grid();
        place.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 80 });
        _splitterColumn = new ColumnDefinition { Width = new GridLength(0) };
        _previewColumn = new ColumnDefinition { Width = new GridLength(0) };
        place.ColumnDefinitions.Add(_splitterColumn);
        place.ColumnDefinitions.Add(_previewColumn);
        place.Children.Add(_textView);
        place.Children.Add(_wpfHexView);
        Grid.SetColumn(_splitter, 1);
        Grid.SetColumn(_preview, 2);
        place.Children.Add(_splitter);
        place.Children.Add(_preview);
        Control = place;
        _previewTimer.Tick += (object sender, EventArgs e) =>
        {
            _previewTimer.Stop();
            RefreshPreview();
        };
        _textView.TextChanged += (object sender, EventArgs e) => SchedulePreview();
        _textView.SyntaxChanged += (object sender, EventArgs e) => SchedulePreview();
        _textView.AppearanceChanged += (object sender, EventArgs e) => SchedulePreview();
        _textView.StateChanged += (object sender, EventArgs e) =>
        {
            if (_previewShown)
            {
                _preview.ShowLine(_textView.CaretPosition.Line);
            }
        };
        Commands.MarkdownPreviewChanged += (object sender, EventArgs e) => UpdatePreviewLayout();
        editor.HexModeChanged += (object sender, EventArgs e) => UpdatePreviewLayout();
        editor.CurrentLanguageChanged += (object sender, EventArgs e) => UpdatePreviewLayout();
        editor.SyntaxChoiceChanged += (object sender, EventArgs e) => UpdatePreviewLayout();
        editor.LoadCompleted += (object sender, EventArgs e) =>
        {
            _textLoaded = true;
            UpdatePreviewLayout();
            SchedulePreview();
        };
        editor.HexViewPlace = this;
        ToolBar = (FrameworkElement)new RemoteFileEditorToolBar(Commands);
        StatusBar = (FrameworkElement)new RemoteFileEditorStatusBar(new RemoteFileEditorStatusSource(editor, Commands));
        Searcher = new TextSearcher(_textView)
        {
            CanReplace = true
        };
        Commands.FindRequested += (object sender, EventArgs e) =>
        {
            if (Editor.IsHexMode)
            {
                ShowHexFind();
                return;
            }
            Searcher.IsReplaceVisible = false;
            Searcher.Activate();
        };
        Commands.HexDialogRequested += (object sender, HexDialogRequestedEventArgs e) => ShowHexDialog(e.Kind);
        Commands.ReplaceRequested += (object sender, EventArgs e) =>
        {
            if (!Editor.IsHexMode)
            {
                Searcher.ActivateReplace();
            }
        };
        editor.HexModeChanged += (object sender, EventArgs e) =>
        {
            if (!Editor.IsHexMode)
            {
                CloseHexFind();
            }
        };
        Title = RemotePathUtils.GetFileName(filePath);
        FilePath = filePath;
        Commands.RefreshState();
    }

    // The refresh of the commands runs after the editor has finished what it is doing (see RemoteFileEditorCommands.ScheduleRefresh).
    private bool PostRefresh(Action refresh)
    {
        if (_textView.IsClosed)
        {
            return false;
        }
        _textView.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
        {
            if (!_textView.IsClosed)
            {
                refresh();
            }
        }));
        return true;
    }

    /// <summary>Ctrl+Shift+M: the key of the Markdown Preview command.</summary>
    internal static bool IsMarkdownPreviewKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == Key.M && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
    }

    /// <summary>Switches the Markdown preview on or off when the file is Markdown and shown as text; false when it cannot be.</summary>
    internal bool TryToggleMarkdownPreview()
    {
        if (!Commands.CaptureState().IsEnabled(EditorToolbarCommand.MarkdownPreview))
        {
            return false;
        }
        Commands.RunCommand(EditorToolbarCommand.MarkdownPreview);
        return true;
    }

    /// <summary>Shows the heading of an anchor in the Markdown preview, at once or when the text has been loaded and shown.</summary>
    internal void ShowMarkdownAnchor(string anchor)
    {
        if (string.IsNullOrEmpty(anchor))
        {
            return;
        }
        if (_previewShown && _textLoaded)
        {
            _preview.ShowAnchor(anchor);
            return;
        }
        _pendingAnchor = anchor;
    }

    // Shows or hides the preview: it is shown while it is switched on, the file is Markdown and the text (not the hex view) is shown.
    private void UpdatePreviewLayout()
    {
        bool show = Commands.MarkdownPreviewOn && !Editor.IsHexMode && Editor.DetectSyntax() == SyntaxHighlighting.Language.Markdown;
        if (show == _previewShown)
        {
            return;
        }
        _previewShown = show;
        if (show)
        {
            _splitterColumn.Width = GridLength.Auto;
            _previewColumn.Width = _previewWidth;
            _previewColumn.MinWidth = 80;
            _splitter.Visibility = Visibility.Visible;
            _preview.Visibility = Visibility.Visible;
            RefreshPreview();
            return;
        }
        // The width the splitter gave it comes back the next time.
        if (_previewColumn.Width.Value > 0)
        {
            _previewWidth = _previewColumn.Width;
        }
        _previewTimer.Stop();
        _previewColumn.MinWidth = 0;
        _previewColumn.Width = new GridLength(0);
        _splitterColumn.Width = new GridLength(0);
        _splitter.Visibility = Visibility.Collapsed;
        bool hadFocus = _preview.IsKeyboardFocusWithin;
        _preview.Visibility = Visibility.Collapsed;
        if (hadFocus)
        {
            Editor.FocusActiveView();
        }
    }

    // The preview is drawn again a moment after the last of a series of changes.
    private void SchedulePreview()
    {
        if (!_previewShown || _textView.IsClosed)
        {
            return;
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RefreshPreview()
    {
        if (!_previewShown || _textView.IsClosed)
        {
            return;
        }
        _preview.SetCodeColors(_textView.TextBackground, _textView.TextForeground, _textView.SyntaxColors);
        _preview.ShowText(_textView.Document.GetText());
        _preview.ShowLine(_textView.CaretPosition.Line);
        if (_pendingAnchor != null && _textLoaded)
        {
            string anchor = _pendingAnchor;
            _pendingAnchor = null;
            _preview.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => _preview.ShowAnchor(anchor)));
        }
    }

    /// <summary>Opens (or brings forward) the modeless find window of the hex view.</summary>
    internal void ShowHexFind()
    {
        if (!Editor.IsHexMode)
        {
            return;
        }
        if (_hexFindWindow == null)
        {
            _hexFindWindow = new HexFindWindow(Editor.HexEditor);
            _hexFindWindow.Closed += (object sender, EventArgs e) => _hexFindWindow = null;
        }
        if (!_hexFindWindow.IsVisible)
        {
            _hexFindWindow.Show();
        }
        _hexFindWindow.Activate();
    }

    /// <summary>Opens a dialog of the hex view (a colour, a byte pattern, the checksums) and applies its answer.</summary>
    internal void ShowHexDialog(HexDialogKind kind)
    {
        if (!Editor.IsHexMode)
        {
            return;
        }
        IHexEditorView hex = Editor.HexEditor;
        try
        {
            switch (kind)
            {
                case HexDialogKind.Highlight:
                {
                    HexHighlight current = hex.HighlightAtCaret;
                    RgbColor color = (current != null) ? RgbColor.FromArgb(current.Argb) : HexViewChoices.HighlightColors[0].Color;
                    int start = hex.HasSelection ? hex.SelectionStart : hex.CaretOffset;
                    int count = hex.HasSelection ? hex.SelectionLength : 1;
                    string target = HexEditingModel.FormatByteCount(count) + " at offset 0x" + start.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                    HexHighlightWindow window = new HexHighlightWindow(color, current?.Label ?? "", target);
                    if (window.ShowDialog() == true)
                    {
                        hex.HighlightSelection(window.SelectedColor, window.Label);
                    }
                    break;
                }
                case HexDialogKind.FillSelection:
                case HexDialogKind.InsertBytes:
                {
                    bool insert = kind == HexDialogKind.InsertBytes;
                    HexBytePatternWindow window = new HexBytePatternWindow(insert, hex.SelectionLength, hex.HasSelection ? hex.SelectionStart : hex.CaretOffset);
                    if (window.ShowDialog() == true)
                    {
                        if (insert)
                        {
                            hex.InsertPattern(window.Count, window.Pattern);
                        }
                        else
                        {
                            hex.FillSelection(window.Pattern);
                        }
                    }
                    break;
                }
                case HexDialogKind.Checksums:
                {
                    byte[] bytes = hex.GetChecksumBytes(out string description);
                    new HexChecksumWindow(HexChecksums.Compute(bytes), description).ShowDialog();
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
        Editor.FocusActiveView();
        Commands.RefreshState();
    }

    void IHexViewPlace.ShowHexView()
    {
        _wpfHexView.Visibility = Visibility.Visible;
        _textView.Visibility = Visibility.Collapsed;
    }

    void IHexViewPlace.HideHexView()
    {
        bool hadFocus = _wpfHexView.IsKeyboardFocusWithin;
        _textView.Visibility = Visibility.Visible;
        _wpfHexView.Visibility = Visibility.Collapsed;
        if (hadFocus)
        {
            // The keyboard goes back to the text.
            _textView.Focus();
        }
    }

    void IHexViewPlace.SetAppearance(RgbColor background, RgbColor foreground, string fontFamily, float fontPoints)
    {
        _wpfHexView.SetColors(background, foreground);
        _wpfHexView.SetFont(fontFamily, fontPoints);
    }

    private void CloseHexFind()
    {
        _hexFindWindow?.Close();
        _hexFindWindow = null;
    }

    public void Close()
    {
        if (Editor.IsModified)
        {
            switch (MessageBoxes.YesNoCancel("Save " + FilePath + "?"))
            {
                case MessageBoxResult.Yes:
                    if (!Editor.TrySave())
                    {
                        return;
                    }
                    break;
                case MessageBoxResult.No:
                    // The changes are given up on purpose: no draft of them is kept.
                    Editor.DiscardDraft();
                    break;
                case MessageBoxResult.Cancel:
                    return;
            }
        }
        _Session.CloseFirstMatchingPage((ITabContext tabContext) => tabContext == this, closeSessionIfEmpty: true);
    }

    public void Dispose()
    {
        CloseHexFind();
        _previewTimer.Dispose();
        Editor.Dispose();
    }

    public void FocusMainControl()
    {
        Editor?.FocusActiveView();
    }

    public void OnSessionInactiveChanged(bool isInactive)
    {
    }

    public void OnSessionReconnected(SshSession session)
    {
    }
}
