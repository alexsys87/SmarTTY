using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Orientation = System.Windows.Controls.Orientation;

namespace SmarTTY.GUI.Backend;

public class EditorTabContext : ITabContext, IDisposable, INotifyPropertyChanged, ICloseableElement, IHexViewPlace
{
    private SessionWrapper _Session;

    public readonly RemoteFileEditor Editor;

    private string _Title;

    // The find window of the hex view (modeless), and whether the WPF scroll bars were hidden for that view.
    private HexFindWindow _hexFindWindow;

    private bool _hexChromeHidden;

    private Visibility _verticalScrollBarVisibility;

    private Visibility _horizontalScrollBarVisibility;

    // The WinForms part of the tab (the editor with its context menu), and the WPF hex view shown in its place (null when the editor has its own).
    private readonly WinFormsHostWrapper _host;

    private readonly HexView.WpfHexEditor _wpfHexView;

    public readonly string FilePath;

    public FrameworkElement Control { get; }

    public FrameworkElement ToolBar { get; }

    public FrameworkElement StatusBar { get; }

    public FileExplorer FileExplorer => null;

    public TextEditorControl EditorControl => Editor.TextControl;

    /// <summary>The editor together with its context menu (the WinForms part of the tab).</summary>
    internal RemoteFileEditorPanel EditorPanel { get; }

    /// <summary>The commands of the editor and their state (the tool bar, the context menus and the keys run them).</summary>
    internal RemoteFileEditorCommands Commands => EditorPanel.Commands;

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

    public FrameworkElement VerticalScrollBar { get; }

    public FrameworkElement HorizontalScrollBar { get; }

    public string ToolTip => FilePath;

    public bool IsNewSessionShortcut => false;

    public event PropertyChangedEventHandler PropertyChanged;

    public EditorTabContext(SessionWrapper sessionWrapper, RemoteFileEditor editor, string filePath)
    {
        WPFScrollBar wPFScrollBar = new WPFScrollBar();
        wPFScrollBar.Orientation = Orientation.Vertical;
        VerticalScrollBar = (FrameworkElement)wPFScrollBar;
        WPFScrollBar wPFScrollBar2 = new WPFScrollBar();
        wPFScrollBar2.Orientation = Orientation.Horizontal;
        HorizontalScrollBar = (FrameworkElement)wPFScrollBar2;
        Editor = editor;
        _Session = sessionWrapper;
        WinFormsHostWrapper winFormsHostWrapper = new WinFormsHostWrapper();
        _host = winFormsHostWrapper;
        EditorPanel = new RemoteFileEditorPanel(editor);
        ((WindowsFormsHost)winFormsHostWrapper).Child = EditorPanel;
        _wpfHexView = editor.HexEditor as HexView.WpfHexEditor;
        if (_wpfHexView != null)
        {
            // The WPF hex view lies over the place of the editor: one of the two is shown (a WPF element cannot be drawn over a WinForms one).
            _wpfHexView.Visibility = Visibility.Collapsed;
            _wpfHexView.ContextMenu = new HexView.HexViewContextMenu(Commands, _wpfHexView, ShowHexDialog, () =>
            {
                Editor.FocusActiveView();
                EditorPanel.RefreshState();
            });
            System.Windows.Controls.Grid place = new System.Windows.Controls.Grid();
            place.Children.Add(winFormsHostWrapper);
            place.Children.Add(_wpfHexView);
            Control = place;
            editor.HexViewPlace = this;
        }
        else
        {
            Control = (FrameworkElement)winFormsHostWrapper;
        }
        ToolBar = (FrameworkElement)new RemoteFileEditorToolBar(Commands);
        StatusBar = (FrameworkElement)new RemoteFileEditorStatusBar(new RemoteFileEditorStatusSource(editor, Commands));
        Searcher = new TextSearcher(editor.TextControl)
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
        editor.TextControl.ReplaceScrollBars((IScrollBar)VerticalScrollBar, (IScrollBar)HorizontalScrollBar);
        editor.HexModeChanged += (object sender, EventArgs e) => UpdateEditorChrome();
        UpdateEditorChrome();
        Title = RemotePathUtils.GetFileName(filePath);
        FilePath = filePath;
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
                    System.Drawing.Color color = (current != null) ? System.Drawing.Color.FromArgb(current.Argb) : HexViewChoices.HighlightColors[0].Color.ToColor();
                    int start = hex.HasSelection ? hex.SelectionStart : hex.CaretOffset;
                    int count = hex.HasSelection ? hex.SelectionLength : 1;
                    string target = HexEditingModel.FormatByteCount(count) + " at offset 0x" + start.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                    HexHighlightWindow window = new HexHighlightWindow(color, current?.Label ?? "", target);
                    if (window.ShowDialog() == true)
                    {
                        hex.HighlightSelection(window.SelectedColor.ToRgb(), window.Label);
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
        EditorPanel.RefreshState();
    }

    void IHexViewPlace.ShowHexView()
    {
        _wpfHexView.Visibility = Visibility.Visible;
        _host.Visibility = Visibility.Collapsed;
    }

    void IHexViewPlace.HideHexView()
    {
        bool hadFocus = _wpfHexView.IsKeyboardFocusWithin;
        _host.Visibility = Visibility.Visible;
        _wpfHexView.Visibility = Visibility.Collapsed;
        if (hadFocus)
        {
            // The keyboard goes back into the WinForms part of the tab, where the editor gives it to the text.
            _host.Focus();
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

    /// <summary>
    /// The hex view draws its own scroll bars: the WPF ones above and below the editor are hidden while it is shown.
    /// The text view gets back the visibility it had.
    /// </summary>
    private void UpdateEditorChrome()
    {
        bool hex = Editor.IsHexMode;
        if (hex && !_hexChromeHidden)
        {
            _verticalScrollBarVisibility = VerticalScrollBar.Visibility;
            _horizontalScrollBarVisibility = HorizontalScrollBar.Visibility;
        }
        else if (!hex && _hexChromeHidden)
        {
            VerticalScrollBar.Visibility = _verticalScrollBarVisibility;
            HorizontalScrollBar.Visibility = _horizontalScrollBarVisibility;
            CloseHexFind();
        }
        if (hex)
        {
            VerticalScrollBar.Visibility = Visibility.Collapsed;
            HorizontalScrollBar.Visibility = Visibility.Collapsed;
        }
        _hexChromeHidden = hex;
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
        // The panel owns the editor; disposing the editor again is harmless.
        EditorPanel.Dispose();
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
