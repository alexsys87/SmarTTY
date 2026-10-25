using System;
using System.Windows;
using SmarTTY.Controls;
using SmarTTY.GUI.Backend;
using SmarTTY.Ssh;
using SmarTTY.Terminal;

namespace SmarTTY.GUI;

/// <summary>
/// Shows a password prompt that a <c>sudo</c> command printed on a shell channel, and lets the user type the password
/// into it. The window embeds the terminal control of the rest of the application, so the prompt looks the same as in
/// a normal tab; it closes when the command finishes.
/// </summary>
public partial class SudoConfirmationWindow : Window
{
    private readonly SshShellChannel _sshShellChannel;

    private readonly TerminalEmulatorControl _terminal;

    private readonly Utf8StreamDecoder _decoder = new Utf8StreamDecoder();

    private SudoConfirmationWindow(SshShellChannel sshShellChannel, string promptText)
    {
        InitializeComponent();
        _sshShellChannel = sshShellChannel;
        _terminal = new TerminalEmulatorControl
        {
            ActiveBufferIndex = 0,
            AutoScroll = true,
            AutoWrap = true,
            BackColor = System.Drawing.Color.Black,
            ForeColor = System.Drawing.Color.LightGray,
            NormalTextColor = System.Drawing.Color.White,
            BellMode = TerminalEmulatorControl.BeepMode.None,
            CaptureDialogKeys = false,
            TabWidth = 8,
            Dock = System.Windows.Forms.DockStyle.Fill,
            Font = new System.Drawing.Font("Courier New", 12f),
            NavigationLocked = true,
            HideCaret = false,
            HideMargins = true,
            HorizontalScrollBarVisible = false,
            VerticalScrollBarVisible = false,
            ShowLineNumbers = true,
            CaretWhenUnfocused = true,
            ReadOnly = true,
            Dispatcher = null
        };
        WinFormsHostWrapper host = new WinFormsHostWrapper();
        host.Child = _terminal;
        TerminalHost.Content = host;

        _terminal.ProcessOutput(promptText + "\r\n");
        sshShellChannel.Finished += OnChannelFinished;
        sshShellChannel.ResizePty(_terminal.TerminalSize.Width, _terminal.TerminalSize.Height);
        sshShellChannel.DataReceived += OnChannelDataReceived;
        _terminal.DataEntered += OnTerminalInput;
        _terminal.TerminalSizeChanged += OnTerminalSizeChanged;
        sshShellChannel.PollingEnabled = true;
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

    private void OnTerminalInput(object sender, byte[] buffer, TerminalEmulatorControl.InputKind inputKind)
    {
        _sshShellChannel.Input.Write(buffer, 0, buffer.Length);
    }

    private void OnTerminalSizeChanged(TerminalEmulatorControl terminal, int width, int height)
    {
        _sshShellChannel.ResizePty(width, height);
    }

    protected override void OnClosed(EventArgs e)
    {
        _sshShellChannel.Finished -= OnChannelFinished;
        _sshShellChannel.DataReceived -= OnChannelDataReceived;
        base.OnClosed(e);
    }
}
