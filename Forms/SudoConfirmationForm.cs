using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.Terminal;
using System.Threading;

namespace SmarTTY.Forms;

public partial class SudoConfirmationForm : FormBase
{

    public SudoConfirmationForm(SshShellChannel sshShellChannel, string promptText)
    {
        SudoConfirmationForm sudoConfirmationForm = this;
        this.InitializeComponent();
        FormIcons.Apply(this, "sudo");
        this.terminalEmulatorControl1.ProcessOutput(promptText + "\r\n");
        Utf8StreamDecoder utf8StreamDecoder = new Utf8StreamDecoder();
        sshShellChannel.Finished += (object sender) =>
        {
            sudoConfirmationForm.BeginInvoke(new ThreadStart(sudoConfirmationForm.Close));
        };
        sshShellChannel.ResizePty(this.terminalEmulatorControl1.TerminalSize.Width, this.terminalEmulatorControl1.TerminalSize.Height);
        sshShellChannel.DataReceived += (object sender, byte[] buffer, int offset, int count, int dataType) =>
        {
            sudoConfirmationForm.terminalEmulatorControl1.EnqueueOutput(utf8StreamDecoder.Decode(buffer, offset, count));
        };
        this.terminalEmulatorControl1.DataEntered += (object sender, byte[] buffer, TerminalEmulatorControl.InputKind inputKind) =>
        {
            sshShellChannel.Input.Write(buffer, 0, buffer.Length);
        };
        this.terminalEmulatorControl1.TerminalSizeChanged += (TerminalEmulatorControl terminalEmulatorControl, int width, int height) =>
        {
            sshShellChannel.ResizePty(width, height);
        };
        sshShellChannel.PollingEnabled = true;
    }
}
