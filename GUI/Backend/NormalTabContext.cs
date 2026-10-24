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
        _Session = sessionWrapper;
        Terminal = term;
        // The terminal is a WinForms control hosted in WPF: files dropped on it never reach the WPF window's own
        // drop handler, so the control has to accept them itself.
        term.AllowDrop = true;
        term.DragEnter += Terminal_DragOver;
        term.DragOver += Terminal_DragOver;
        term.DragDrop += Terminal_DragDrop;
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
        bool acceptFiles = !_Session.IsDevice && e.Data != null && e.Data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop);
        e.Effect = acceptFiles ? System.Windows.Forms.DragDropEffects.Copy : System.Windows.Forms.DragDropEffects.None;
    }

    private void Terminal_DragDrop(object sender, System.Windows.Forms.DragEventArgs e)
    {
        if (_Session.IsDevice || e.Data == null || !(e.Data.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] files) || files.Length == 0)
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

    public void OnSessionInactiveChanged(bool isInactive)
    {
        Terminal.HideCaret = isInactive;
    }

    public void OnSessionReconnected(SshSession session)
    {
        // Only a classic terminal owns a shell channel. A Smart Terminal tab runs every command in a channel of its
        // own through the session context, so it picks the new session up by itself and must not get a shell.
        if (Terminal.Tag is TerminalTag terminalTag && terminalTag.Channel != null)
        {
            SshShellChannel previousChannel = terminalTag.Channel;
            Terminal.BeginNewSession();
            Terminal.HideCaret = false;
            try
            {
                SshShellChannel sshShellChannel = session.OpenShellChannel(null, "xterm", ExtendedDataMode.Normal, utf8Tty: !Terminal.ByteOrientedLineEditing);
                sshShellChannel.ExtendedDataMode = ExtendedDataMode.Merge;
                terminalTag.Channel = sshShellChannel;
                terminalTag.ShellOpenFailed = false;
                _Session.AttachChannelToControl(Terminal, sshShellChannel, isAutoLaunchedCommand: false);
            }
            catch (Exception exception)
            {
                // Keep the other tabs reconnecting; this one shows why it has no shell. Its channel is the old, closed
                // one, and the session counts as healthy, so nothing else would ever try again: a key press does.
                terminalTag.ShellOpenFailed = true;
                Terminal.ProcessOutput(TerminalEmulatorControl.Colorize("Failed to open a shell: " + exception.Message + ". Press any key to try again.", 1) + "\r\n");
            }
            DisposeChannel(previousChannel);
        }
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
