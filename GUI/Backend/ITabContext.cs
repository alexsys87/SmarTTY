using SmarTTY.Ssh;
using SmarTTY.Controls;
using System;
using System.Windows;

namespace SmarTTY.GUI.Backend;

public interface ITabContext : IDisposable
{
    FrameworkElement Control { get; }

    TextEditorControl EditorControl { get; }

    string Title { get; }

    IconType Icon { get; }

    TextSearcher Searcher { get; }

    string ToolTip { get; }

    FileExplorer FileExplorer { get; }

    FrameworkElement VerticalScrollBar { get; }

    FrameworkElement HorizontalScrollBar { get; }

    void FocusMainControl();

    void OnSessionInactiveChanged(bool isInactive);

    void OnSessionReconnected(SshSession session);
}
