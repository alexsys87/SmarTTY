using SmarTTY.Ssh;
using SmarTTY.Controls;
using System;
using System.Windows;

namespace SmarTTY.GUI.Backend;

public interface ITabContext : IDisposable
{
    FrameworkElement Control { get; }

    /// <summary>The WPF tool bar above the tab content, or null for tabs without one.</summary>
    FrameworkElement ToolBar { get; }

    /// <summary>The WPF status bar below the tab content, or null for tabs without one.</summary>
    FrameworkElement StatusBar { get; }

    TextEditorControl EditorControl { get; }

    string Title { get; }

    IconType Icon { get; }

    TextSearcher Searcher { get; }

    string ToolTip { get; }

    bool IsNewSessionShortcut { get; }

    FileExplorer FileExplorer { get; }

    FrameworkElement VerticalScrollBar { get; }

    FrameworkElement HorizontalScrollBar { get; }

    void FocusMainControl();

    void OnSessionInactiveChanged(bool isInactive);

    void OnSessionReconnected(SshSession session);
}
