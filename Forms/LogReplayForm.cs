using SmarTTY.Controls;
using System;

namespace SmarTTY.Forms;

public partial class LogReplayForm : DpiAwareForm
{
    private readonly string _logText;

    private readonly byte[] _rawBytes;

    private readonly TerminalEmulatorControl _terminalEmulatorControl;

    // When set, the log is the raw byte stream of a device (COM / raw TCP / telnet) terminal and is replayed through
    // the device terminal (hex dump in HEX mode, decoded text otherwise) instead of the plain text terminal above.
    private readonly DeviceTerminalControl _deviceTerminal;

    private readonly int _length;

    private int _position;

    private bool _isPaused;

    private bool _finished;

    public LogReplayForm(string logText, TerminalEmulatorControl terminalEmulatorControl)
        : this(terminalEmulatorControl)
    {
        _logText = logText ?? throw new ArgumentNullException(nameof(logText));
        _length = _logText.Length;
    }

    public LogReplayForm(byte[] rawBytes, DeviceTerminalControl deviceTerminal)
        : this(null)
    {
        _rawBytes = rawBytes ?? throw new ArgumentNullException(nameof(rawBytes));
        _deviceTerminal = deviceTerminal ?? throw new ArgumentNullException(nameof(deviceTerminal));
        _length = _rawBytes.Length;
    }

    private LogReplayForm(TerminalEmulatorControl terminalEmulatorControl)
    {
        InitializeComponent();
        _terminalEmulatorControl = terminalEmulatorControl;
        Text = "Replay log";
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        if (_position >= _length)
        {
            if (_length > 0 && !_finished)
            {
                // Playback finished: let the next button click start it from the beginning.
                _finished = true;
                button1.Text = "Replay";
            }
            return;
        }
        if (_isPaused)
        {
            return;
        }
        int chunkSize = Math.Min(_length - _position, Math.Max(1, trackBar1.Value));
        if (_deviceTerminal != null)
        {
            // Feed the exact bytes of this step as a brand-new slice (starting at offset 0), so the hex dump appends
            // them verbatim and a multi-byte character torn by a previous partial step is handed over intact.
            byte[] chunk = new byte[chunkSize];
            Buffer.BlockCopy(_rawBytes, _position, chunk, 0, chunkSize);
            _deviceTerminal.ReplayReceivedData(chunk, chunkSize);
        }
        else
        {
            // The log is replayed into the live terminal: queries recorded in it must not be answered to the session.
            _terminalEmulatorControl.SuppressReplies = true;
            try
            {
                _terminalEmulatorControl.ProcessOutput(_logText.Substring(_position, chunkSize));
            }
            finally
            {
                _terminalEmulatorControl.SuppressReplies = false;
            }
        }
        _position += chunkSize;
    }

    private void button1_Click(object sender, EventArgs e)
    {
        if (_finished)
        {
            ResetPlayback();
            return;
        }
        _isPaused = !_isPaused;
        button1.Text = _isPaused ? "Resume" : "Pause";
    }

    private void button2_Click(object sender, EventArgs e)
    {
        ResetPlayback();
    }

    private void ResetPlayback()
    {
        _position = 0;
        _finished = false;
        _isPaused = false;
        // A multi-byte character split by the previous partial step must not corrupt the re-decoded first step.
        _deviceTerminal?.ResetReplayDecoder();
        button1.Text = "Pause";
    }
}
