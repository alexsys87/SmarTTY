using SmarTTY.Ssh;
using SmarTTY.Controls;
using System;
using System.Windows;

namespace SmarTTY.GUI.Backend;

public class PseudoTabContext : ITabContext, IDisposable
{
    public Action<ITabContext> ActivationHandler;

    public IconType Icon { get; set; } = IconType.Add;

    public FrameworkElement Control { get; }

    public string Title => null;

    public TextEditorControl EditorControl => null;

    public TextSearcher Searcher => null;

    public FrameworkElement VerticalScrollBar => null;

    public FrameworkElement HorizontalScrollBar => null;

    public bool IsNewSessionShortcut { get; set; }

    public FileExplorer FileExplorer => null;

    public string ToolTip { get; set; }

    public void Dispose()
    {
    }

    public void FocusMainControl()
    {
    }

    public void OnSessionInactiveChanged(bool isInactive)
    {
    }

    public void OnSessionReconnected(SshSession session)
    {
    }
}
