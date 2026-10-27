using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.IO;
using SmarTTY.TextEditing;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Orientation = System.Windows.Controls.Orientation;

namespace SmarTTY.GUI.Backend;

public class EditorTabContext : ITabContext, IDisposable, INotifyPropertyChanged, ICloseableElement
{
    private SessionWrapper _Session;

    public readonly RemoteFileEditor Editor;

    private string _Title;

    // The find window of the hex view (modeless), and whether the WPF scroll bars were hidden for that view.
    private HexFindWindow _hexFindWindow;

    private bool _hexChromeHidden;

    private Visibility _verticalScrollBarVisibility;

    private Visibility _horizontalScrollBarVisibility;

    public readonly string FilePath;

    public FrameworkElement Control { get; }

    public FrameworkElement ToolBar { get; }

    public FrameworkElement StatusBar { get; }

    public FileExplorer FileExplorer => null;

    public TextEditorControl EditorControl => Editor;

    /// <summary>The editor together with its context menu (the WinForms part of the tab).</summary>
    internal RemoteFileEditorPanel EditorPanel { get; }

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
        EditorPanel = new RemoteFileEditorPanel(editor);
        ((WindowsFormsHost)winFormsHostWrapper).Child = EditorPanel;
        Control = (FrameworkElement)winFormsHostWrapper;
        ToolBar = (FrameworkElement)new RemoteFileEditorToolBar(EditorPanel);
        StatusBar = (FrameworkElement)new RemoteFileEditorStatusBar(editor, EditorPanel);
        Searcher = new TextSearcher(editor)
        {
            CanReplace = true
        };
        EditorPanel.FindRequested += (object sender, EventArgs e) =>
        {
            if (Editor.IsHexMode)
            {
                ShowHexFind();
                return;
            }
            Searcher.IsReplaceVisible = false;
            Searcher.Activate();
        };
        EditorPanel.ReplaceRequested += (object sender, EventArgs e) =>
        {
            if (!Editor.IsHexMode)
            {
                Searcher.ActivateReplace();
            }
        };
        editor.ReplaceScrollBars((IScrollBar)VerticalScrollBar, (IScrollBar)HorizontalScrollBar);
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
