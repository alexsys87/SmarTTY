using SmarTTY.Terminal;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using VisualTTY;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// Tools > Quick commands: a script in the menu of a COM-port or Telnet terminal. An SSH tab uploads a script over its own channel;
// a device has only the line of its terminal, so the script is typed into the shell of the device with "cat":
//
//   1. the command line "/bin/sh -c 'umask 022; trap ...; cat > name.smartty-part && chmod 755 ... && mv -f ...'" ends with CR;
//   2. the lines of the script follow, ended with LF and paced like a slow paste (the pauses of Settings > Paste), straight to the
//      device (the local input line of the terminal is off meanwhile);
//   3. Ctrl-D at the start of a line ends the input of cat, and chmod and mv put the finished file under its name;
//   4. when the prompt of the device is back, "./name" is put into the command line; Enter runs it.
//
// Esc (or a lost connection) ends the upload with Ctrl-C: cat stops and the trap of the command removes the temporary file.
// What the tty of a device does with such input (CR and LF, Ctrl-D, Ctrl-C, long lines, UTF-8, tabs) was tried on a pty with dash,
// bash and BusyBox ash; the rules that follow from it are in QuickCommandRules.ValidateForSerial and in the comments below.
public partial class DeviceTerminalControl
{
    private static readonly Encoding QuickScriptEncoding = new UTF8Encoding(false);

    // The command that is put into the command line when the prompt of the device is back after an upload, and what waits for it.
    private string _quickScriptInvocation;

    private System.Windows.Forms.Timer _quickScriptPromptTimer;

    // How long the prompt is waited for after the upload (chmod and mv are quick, but a slow device may be busy).
    private const int QuickScriptPromptTimeoutMs = 20000;

    /// <summary>
    /// Types the script of a quick command into the shell of the device and, when it is stored, puts "./name" into the command
    /// line. Runs on the UI thread; the transfer goes on in the background (progress window, Esc cancels).
    /// </summary>
    private void UploadQuickScript(QuickCommand item)
    {
        if (item == null || item.Kind != QuickCommandKind.Script || IsLineClaimed || (_pasteJob != null && _pasteJob.IsActive))
        {
            return;
        }
        string caption = "Quick commands";
        string problem = QuickCommandRules.ValidateForSerial(item);
        if (problem != null)
        {
            ThemedMessageBox.Show(item.MenuText + ": " + problem, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (!QuickScriptPossibleNow(caption))
        {
            return;
        }
        byte[] data = QuickCommandRules.ScriptBytes(item.Text);
        int lines = 0;
        foreach (byte value in data)
        {
            if (value == (byte)'\n')
            {
                lines++;
            }
        }
        string question = "Type the script " + item.FileName + " (" + lines + (lines == 1 ? " line, " : " lines, ") + data.Length + " bytes) into the shell of the device?" + Environment.NewLine + Environment.NewLine
            + "It is saved in the current folder of the device with cat, and a file with this name there is replaced. Esc cancels the upload. The shell of the device needs cat, chmod and mv (BusyBox has them).";
        if (ThemedMessageBox.Show(question, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        // The dialog let the device talk meanwhile: it may not be at the prompt any more.
        if (!QuickScriptPossibleNow(caption))
        {
            return;
        }
        AsyncTransportConnection connection = _asyncTransportConnection;
        string command = QuickCommandRules.BuildSerialUploadCommand(QuickCommandRules.RemotePath(null, item.FileName));
        string invocation = QuickCommandRules.BuildInvocation(item.FileName);
        string text = QuickScriptEncoding.GetString(data);
        // The settings of a slow paste, without waiting for an echo or a prompt: cat prints no prompt, and the echo of a line with a
        // tab inside is not told apart reliably. Only the pauses pace the lines; a line is never sent without a pause after it.
        PasteSettings pasteOptions = PasteGuard.OptionsOf(_commonSettings);
        PasteSettings pace = new PasteSettings
        {
            SlowChunkSize = pasteOptions.SlowChunkSize,
            SlowCharDelayMs = pasteOptions.SlowCharDelayMs,
            SlowLineDelayMs = Math.Max(pasteOptions.SlowLineDelayMs, 20),
            SlowWait = PasteSettings.SlowWaitMode.None
        };
        // Every line ends with LF (what the tty takes as the end of a line, whatever its ICRNL is). The command line ends with CR
        // only, even for a connection whose Enter is CR LF: the LF would be the first line of the file, in front of the "#!".
        List<PasteStep> steps = PasteModes.BuildSteps(text, pace.SlowChunkSize, "\n", splitLines: true);
        string enter = GetLineEnding(_deviceTerminalSettings.LineEnding);
        if (enter == null || enter == "\r\n")
        {
            enter = "\r";
        }
        if (!_serialTerminalControl.SubmitCommandNow(command, enter))
        {
            ThemedMessageBox.Show("The command could not be sent: the shell of the device is not at its prompt.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        CancelQuickScriptInvocation();
        // The command line has been handed to the connection by now: this terminal has no input dispatcher (Dispatcher = null), so
        // SendText writes at once, and the first line of the script, sent next, cannot overtake it. From here the keys go to the
        // device as they are, and the local input line stays off until the upload is over (a line of the echo that looks like a
        // prompt must not bring it back in the middle of the text).
        _serialTerminalControl.InputLineDisabled = true;
        _pasteJob = new PasteJob(this, "Typing " + item.FileName + " into the device", steps,
            (string piece, Action written) =>
            {
                byte[] bytes = QuickScriptEncoding.GetBytes(piece);
                return SendToConnection(connection, bytes, bytes.Length, (bool ok) => written());
            },
            pace, true,
            (PasteJob.Outcome outcome, string message) => FinishQuickScript(outcome, message, connection, caption, invocation));
        _pasteJob.Start();
    }

    // The device can take a script only at the prompt of its shell, with nothing typed in the command line yet (a half-typed line
    // would be part of the command that starts the upload).
    private bool QuickScriptPossibleNow(string caption)
    {
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("The device is not connected.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (!_serialTerminalControl.CanSubmitCommandNow)
        {
            ThemedMessageBox.Show("A script can be typed into the device only while its shell shows the prompt and nothing is typed in the command line. Wait for the prompt (or clear the line) and choose the script again.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        return true;
    }

    private void FinishQuickScript(PasteJob.Outcome outcome, string message, AsyncTransportConnection connection, string caption, string invocation)
    {
        _pasteJob = null;
        if (outcome == PasteJob.Outcome.Completed)
        {
            // The text ends with a line break, so this Ctrl-D is at the start of a line: it ends the input of cat.
            SendQuickScriptKey(connection, 0x04);
            // The input line comes back with the prompt that the device prints when chmod and mv are done.
            _serialTerminalControl.InputLineDisabled = false;
            WaitForPromptToInsert(invocation);
            return;
        }
        // Cancelled (Esc) or failed: Ctrl-C ends cat, and the command removes its temporary file.
        SendQuickScriptKey(connection, 0x03);
        _serialTerminalControl.InputLineDisabled = false;
        if (outcome == PasteJob.Outcome.Failed)
        {
            ThemedMessageBox.Show(message, caption, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        }
    }

    // One control character to the connection that the upload used; nothing when that connection is gone or was replaced.
    private void SendQuickScriptKey(AsyncTransportConnection connection, byte key)
    {
        try
        {
            if (connection != null && ReferenceEquals(connection, _asyncTransportConnection))
            {
                SendToConnection(connection, new byte[] { key }, 1);
            }
        }
        catch (Exception)
        {
            // The connection ended while the upload was cancelled: there is nobody to tell.
        }
    }

    // Puts the command into the command line when the input line is back (the device has printed its prompt); gives up after a while.
    private void WaitForPromptToInsert(string invocation)
    {
        CancelQuickScriptInvocation();
        _quickScriptInvocation = invocation;
        _serialTerminalControl.InputLineStateChanged += OnQuickScriptPromptBack;
        _quickScriptPromptTimer = new System.Windows.Forms.Timer { Interval = QuickScriptPromptTimeoutMs };
        _quickScriptPromptTimer.Tick += (object sender, EventArgs e) => CancelQuickScriptInvocation();
        _quickScriptPromptTimer.Start();
    }

    private void OnQuickScriptPromptBack(object sender, EventArgs e)
    {
        if (!_serialTerminalControl.HasInputLine || _quickScriptInvocation == null)
        {
            return;
        }
        string invocation = _quickScriptInvocation;
        CancelQuickScriptInvocation();
        // Not from inside the processing of the output that showed the prompt.
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(new Action(() =>
            {
                if (_serialTerminalControl.HasInputLine && _serialTerminalControl.CanSubmitCommandNow)
                {
                    _serialTerminalControl.InsertCommand(invocation);
                }
            }));
        }
    }

    private void CancelQuickScriptInvocation()
    {
        _quickScriptInvocation = null;
        _serialTerminalControl.InputLineStateChanged -= OnQuickScriptPromptBack;
        if (_quickScriptPromptTimer != null)
        {
            _quickScriptPromptTimer.Stop();
            _quickScriptPromptTimer.Dispose();
            _quickScriptPromptTimer = null;
        }
    }
}
