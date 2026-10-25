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
            Searcher.IsReplaceVisible = false;
            Searcher.Activate();
        };
        EditorPanel.ReplaceRequested += (object sender, EventArgs e) => Searcher.ActivateReplace();
        editor.ReplaceScrollBars((IScrollBar)VerticalScrollBar, (IScrollBar)HorizontalScrollBar);
        Title = RemotePathUtils.GetFileName(filePath);
        FilePath = filePath;
    }

    public void Close()
    {
        if (((EditableTextBrowser)Editor).IsModified)
        {
            switch (MessageBoxes.YesNoCancel("Save " + FilePath + "?"))
            {
                case DialogResult.Yes:
                    if (!Editor.TrySave())
                    {
                        return;
                    }
                    break;
                case DialogResult.No:
                    // The changes are given up on purpose: no draft of them is kept.
                    Editor.DiscardDraft();
                    break;
                case DialogResult.Cancel:
                    return;
            }
        }
        _Session.CloseFirstMatchingPage((ITabContext tabContext) => tabContext == this, closeSessionIfEmpty: true);
    }

    public void Dispose()
    {
        // The panel owns the editor; disposing the editor again is harmless.
        EditorPanel.Dispose();
        Editor.Dispose();
    }

    public void FocusMainControl()
    {
        Editor?.Focus();
    }

    public void OnSessionInactiveChanged(bool isInactive)
    {
    }

    public void OnSessionReconnected(SshSession session)
    {
    }
}
