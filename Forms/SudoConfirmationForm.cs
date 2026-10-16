using ManagedLibSSH2;
using SmarTTY.Controls;
using SmarTTY.Terminal;
using System.Threading;

namespace SmarTTY.Forms;

public partial class SudoConfirmationForm : FormBase
{

    public SudoConfirmationForm(SshShellChannel sshShellChannel, string text)
    {
        SudoConfirmationForm sudoConfirmationForm = this;
        this.InitializeComponent();
        this.terminalEmulatorControl1.ProcessOutput(text + "\r\n");
        Utf8StreamDecoder utf8StreamDecoder = new Utf8StreamDecoder();
        sshShellChannel.Finished += (object obj) =>
        {
            sudoConfirmationForm.BeginInvoke(new ThreadStart(sudoConfirmationForm.Close));
        };
        sshShellChannel.ResizePty(this.terminalEmulatorControl1.TerminalSize.Width, this.terminalEmulatorControl1.TerminalSize.Height);
        sshShellChannel.DataReceived += (object obj, byte[] array, int num, int num2, int num3) =>
        {
            sudoConfirmationForm.terminalEmulatorControl1.EnqueueOutput(utf8StreamDecoder.Decode(array, num, num2));
        };
        this.terminalEmulatorControl1.DataEntered += (object obj, byte[] array, TerminalEmulatorControl.InputKind inputKind) =>
        {
            sshShellChannel.Input.Write(array, 0, array.Length);
        };
        this.terminalEmulatorControl1.TerminalSizeChanged += (TerminalEmulatorControl terminalEmulatorControl, int num, int num2) =>
        {
            sshShellChannel.ResizePty(num, num2);
        };
        sshShellChannel.PollingEnabled = true;
    }
}
