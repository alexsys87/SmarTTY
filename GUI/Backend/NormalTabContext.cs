using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;

namespace SmarTTY.GUI.Backend;

public class NormalTabContext : ITabContext, IDisposable, INotifyPropertyChanged, ICloseableElement
{
    private SessionWrapper _Session;

    public readonly TerminalEmulatorControl Terminal;

    private string _Title;

    public TextSearcher Searcher { get; }

    public FrameworkElement Control { get; }

    public FrameworkElement ToolBar => null;

    public FrameworkElement StatusBar => null;

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

    public IconType Icon
    {
        get
        {
            if (!(Terminal is SshTerminalControl))
            {
                return IconType.Terminal;
            }
            return IconType.VirtualTerminal;
        }
    }

    public TextEditorControl EditorControl => Terminal;

    public FrameworkElement VerticalScrollBar { get; }

    public FrameworkElement HorizontalScrollBar { get; }

    public string ToolTip => Title;

    public bool IsNewSessionShortcut => false;

    public FileExplorer FileExplorer { get; }

    public event PropertyChangedEventHandler PropertyChanged;

    public NormalTabContext(SessionWrapper sessionWrapper, TerminalEmulatorControl term)
    {
        WPFScrollBar wPFScrollBar = new WPFScrollBar();
        wPFScrollBar.Orientation = Orientation.Vertical;
        VerticalScrollBar = (FrameworkElement)wPFScrollBar;
        WPFScrollBar wPFScrollBar2 = new WPFScrollBar();
        wPFScrollBar2.Orientation = Orientation.Horizontal;
        HorizontalScrollBar = (FrameworkElement)wPFScrollBar2;
        NormalTabContext normalTabContext = this;
        if (term is SshTerminalControl vtc)
        {
            FileExplorer = new FileExplorer(vtc)
            {
                IsVisible = sessionWrapper.VTSettings.ShowFileExplorer,
                PackageManagerProvider = () => sessionWrapper.LocalRepoManager
            };
        }
        else if (sessionWrapper.IsWindowsHost)
        {
            // A classic terminal on a Windows host: there is no Smart Terminal to follow, so the list has a directory of its own.
            WindowsFileListHost fileListHost = new WindowsFileListHost(() => sessionWrapper.Session);
            FileExplorer = new FileExplorer(fileListHost)
            {
                IsVisible = sessionWrapper.VTSettings.ShowFileExplorer
            };
            fileListHost.Start();
            // cmd.exe and PowerShell do not report their directory: it is read from their prompt, so the list follows a "cd".
            SmarTTY.Terminal.TerminalDirectoryWatcher directoryWatcher = new SmarTTY.Terminal.TerminalDirectoryWatcher();
            directoryWatcher.DirectoryReported += fileListHost.ReportTerminalDirectory;
            term.OutputReceived += directoryWatcher.Observe;
        }
        _Session = sessionWrapper;
        Terminal = term;
        // The terminal is a WinForms control hosted in WPF: files dropped on it never reach the WPF window's own
        // drop handler, so the control has to accept them itself.
        term.AllowDrop = true;
        term.DragEnter += Terminal_DragOver;
        term.DragOver += Terminal_DragOver;
        term.DragDrop += Terminal_DragDrop;
        term.ClipboardFilesPasted += Terminal_ClipboardFilesPasted;
        WinFormsHostWrapper winFormsHostWrapper = new WinFormsHostWrapper();
        ((WindowsFormsHost)winFormsHostWrapper).Child = term;
        Control = (FrameworkElement)winFormsHostWrapper;
        Searcher = new TextSearcher(term);
        Title = term.Title;
        term.ReplaceScrollBars((IScrollBar)VerticalScrollBar, (IScrollBar)HorizontalScrollBar);
        term.TitleChanged += (object sender, EventArgs e) =>
        {
            normalTabContext.Title = term.Title;
        };
    }

    public void FocusMainControl()
    {
        Terminal?.Focus();
    }

    private void Terminal_DragOver(object sender, System.Windows.Forms.DragEventArgs e)
    {
        bool acceptFiles = !FileListControl.IsDraggingOut && !_Session.IsDevice && e.Data != null && e.Data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop);
        e.Effect = acceptFiles ? System.Windows.Forms.DragDropEffects.Copy : System.Windows.Forms.DragDropEffects.None;
    }

    private void Terminal_DragDrop(object sender, System.Windows.Forms.DragEventArgs e)
    {
        if (FileListControl.IsDraggingOut || _Session.IsDevice || e.Data == null || !(e.Data.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] files) || files.Length == 0)
        {
            return;
        }
        // Start the upload after the drop has finished, so that Explorer is not kept waiting while the remote
        // directory is being determined.
        Terminal.BeginInvoke((Action)(() =>
        {
            try
            {
                _Session.UploadFiles(files, this);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }));
    }

    // Files copied in Explorer and pasted into the terminal: the same upload as for dropped files, or their paths as text.
    private void Terminal_ClipboardFilesPasted(TerminalEmulatorControl terminal, string[] files)
    {
        if (_Session.IsDevice || files == null || files.Length == 0)
        {
            return;
        }
        string what = files.Length == 1 ? "\"" + System.IO.Path.GetFileName(files[0]) + "\"" : files.Length + " files";
        System.Windows.Forms.DialogResult answer = ThemedMessageBox.Show(
            terminal,
            "The clipboard holds " + what + ".\n\nYes: upload to the server (like dropping the files on the terminal).\nNo: paste the local path" + (files.Length == 1 ? "" : "s") + " as text.",
            "Paste",
            System.Windows.Forms.MessageBoxButtons.YesNoCancel,
            System.Windows.Forms.MessageBoxIcon.Question);
        try
        {
            if (answer == System.Windows.Forms.DialogResult.Yes)
            {
                _Session.UploadFiles(files, this);
            }
            else if (answer == System.Windows.Forms.DialogResult.No)
            {
                // A path with a space is quoted; several paths are separated by a space.
                string paths = string.Join(" ", System.Linq.Enumerable.Select(files, file => file.IndexOf(' ') >= 0 ? "\"" + file + "\"" : file));
                terminal.PasteText(paths);
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    public void OnSessionInactiveChanged(bool isInactive)
    {
        Terminal.HideCaret = isInactive;
    }

    public void OnSessionReconnected(SshSession session)
    {
        FileExplorer?.OnSessionReconnected();
        // Only a classic terminal owns a shell channel. A Smart Terminal tab runs every command in a channel of its
        // own through the session context, so it picks the new session up by itself and must not get a shell.
        if (Terminal.Tag is TerminalTag terminalTag && terminalTag.Channel != null)
        {
            SshShellChannel previousChannel = terminalTag.Channel;
            Terminal.BeginNewSession();
            Terminal.HideCaret = false;
            // Opening a shell waits for the server (and, in PtySetupGate, up to 5 s for the helper commands that run in
            // the background after a reconnect), once for every tab: it must not run on the UI thread. The tab keeps its
            // old, closed channel until the new one is there; input meanwhile is dropped by the input handler.
            int generation = ++_ReopenGeneration;
            _OpeningShell = true;
            bool utf8Tty = !Terminal.ByteOrientedLineEditing;
            ThreadPool.QueueUserWorkItem((object state) =>
            {
                SshShellChannel opened = null;
                Exception error = null;
                try
                {
                    opened = session.OpenShellChannel(null, "xterm", ExtendedDataMode.Normal, utf8Tty: utf8Tty);
                    opened.ExtendedDataMode = ExtendedDataMode.Merge;
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                try
                {
                    // The WPF dispatcher, not Terminal.BeginInvoke: the WinForms control of a tab that was never shown has
                    // no window handle yet, and its shell must be reopened all the same.
                    Application.Current.Dispatcher.BeginInvoke((Action)(() => CompleteShellReopen(session, terminalTag, previousChannel, opened, error, generation)));
                }
                catch (Exception)
                {
                    // The application is shutting down: nobody will use the new shell.
                    DisposeChannel(opened);
                }
            });
        }
    }

    /// <summary>True while the shell of this tab is being opened after a reconnect (the key press retry must not start a second one).</summary>
    public bool IsOpeningShell => _OpeningShell;

    private int _ReopenGeneration;

    private bool _OpeningShell;

    /// <summary>The end of <see cref="OnSessionReconnected"/> on the UI thread.</summary>
    private void CompleteShellReopen(SshSession session, TerminalTag terminalTag, SshShellChannel previousChannel, SshShellChannel opened, Exception error, int generation)
    {
        if (generation != _ReopenGeneration || Terminal.IsDisposed)
        {
            // A newer reconnect (or retry) took over this tab, or the tab is gone: this result is of no use.
            DisposeChannel(opened);
            DisposeChannel(previousChannel);
            return;
        }
        _OpeningShell = false;
        try
        {
            if (error != null)
            {
                throw error;
            }
            terminalTag.Channel = opened;
            terminalTag.ShellOpenFailed = false;
            _Session.AttachChannelToControl(Terminal, opened, isAutoLaunchedCommand: false);
        }
        catch (Exception exception)
        {
            // Keep the other tabs reconnecting; this one shows why it has no shell. Its channel is the old, closed
            // one, and the session counts as healthy, so nothing else would ever try again: a key press does.
            terminalTag.ShellOpenFailed = true;
            DisposeChannel(opened);
            Terminal.ProcessOutput(TerminalEmulatorControl.Colorize("Failed to open a shell: " + exception.Message + ". Press any key to try again.", 1) + "\r\n");
        }
        DisposeChannel(previousChannel);
    }

    public void Dispose()
    {
        // Closing the tab ends its remote shell; otherwise the channel would stay open until the whole session
        // is closed and keep receiving output for a terminal that no longer exists.
        if (Terminal.Tag is TerminalTag terminalTag)
        {
            DisposeChannel(terminalTag.Channel);
        }
        Terminal.Dispose();
    }

    // Closing a channel waits for the server to confirm it, so it is done off the UI thread.
    private static void DisposeChannel(SshShellChannel channel)
    {
        if (channel == null)
        {
            return;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                channel.Dispose();
            }
            catch
            {
            }
        });
    }

    public void Close()
    {
        _Session.CloseFirstMatchingPage((ITabContext tabContext) => tabContext == this, closeSessionIfEmpty: true);
    }
}
