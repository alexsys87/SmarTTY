using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// The tab of a file on the server (stage 3E-4: on WPF): the WPF text view and the WPF hex view of a <see cref="RemoteFileEditor"/>, one of them
/// shown, with the tool bar and the status bar of the editor and their context menus, all running the same <see cref="RemoteFileEditorCommands"/>.
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
        System.Windows.Controls.Grid place = new System.Windows.Controls.Grid();
        place.Children.Add(_textView);
        place.Children.Add(_wpfHexView);
        Control = place;
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
