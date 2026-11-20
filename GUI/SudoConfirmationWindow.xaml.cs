using VisualTTY;
using System;
using System.Windows;
using SmarTTY.Controls;
using SmarTTY.GUI.TerminalView;
using SmarTTY.Ssh;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Input;

namespace SmarTTY.GUI;

/// <summary>
/// Shows a password prompt that a <c>sudo</c> command printed on a shell channel, and lets the user type the password
/// into it. The window embeds the WPF terminal of the tabs, so the prompt looks the same as in a normal tab; it closes when
/// the command finishes.
/// </summary>
public partial class SudoConfirmationWindow : Window
{
    private readonly SshShellChannel _sshShellChannel;

    private readonly WpfTerminal _terminal;

    private readonly Utf8StreamDecoder _decoder = new Utf8StreamDecoder();

    private SudoConfirmationWindow(SshShellChannel sshShellChannel, string promptText)
    {
        InitializeComponent();
        _sshShellChannel = sshShellChannel;
        // The WPF terminal of the tabs, with no bell and no scroll bar; it is a terminal like a tab, so the prompt looks the same.
        _terminal = new WpfTerminal
        {
            BellMode = BeepMode.None,
            ScrollBarVisible = false,
            CaretWhenUnfocused = true
        };
        _terminal.View.TerminalFontFamily = new System.Windows.Media.FontFamily("Courier New");
        _terminal.View.TerminalFontSize = 16;
        TerminalHost.Content = _terminal;

        _terminal.ProcessOutput(promptText + "\r\n");
        sshShellChannel.Finished += OnChannelFinished;
        sshShellChannel.ResizePty(_terminal.GridSize.X, _terminal.GridSize.Y);
        sshShellChannel.DataReceived += OnChannelDataReceived;
        _terminal.DataEntered += OnTerminalInput;
        _terminal.TerminalSizeChanged += OnTerminalSizeChanged;
        sshShellChannel.PollingEnabled = true;
        Loaded += (sender, e) => _terminal.FocusTerminal();
    }

    /// <summary>Shows the prompt; the window closes itself when the command ends.</summary>
    public static void Show(Window owner, SshShellChannel sshShellChannel, string promptText)
    {
        SudoConfirmationWindow window = new SudoConfirmationWindow(sshShellChannel, promptText);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        window.ShowDialog();
    }

    private void OnChannelFinished(object sender)
    {
        Dispatcher.BeginInvoke((Action)Close);
    }

    private void OnChannelDataReceived(object sender, byte[] buffer, int offset, int count, int dataType)
    {
        _terminal.EnqueueOutput(_decoder.Decode(buffer, offset, count));
    }

    private void OnTerminalInput(WpfTerminal sender, byte[] buffer, TerminalInputKind inputKind)
    {
        _sshShellChannel.Input.Write(buffer, 0, buffer.Length);
    }

    private void OnTerminalSizeChanged(WpfTerminal terminal, int width, int height)
    {
        _sshShellChannel.ResizePty(width, height);
    }

    protected override void OnClosed(EventArgs e)
    {
        _sshShellChannel.Finished -= OnChannelFinished;
        _sshShellChannel.DataReceived -= OnChannelDataReceived;
        _terminal.Dispose();
        base.OnClosed(e);
    }
}
