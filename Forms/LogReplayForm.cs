using SmarTTY.Controls;
using System;

namespace SmarTTY.Forms;

public partial class LogReplayForm : DpiAwareForm
{
    private string _logText;

    private TerminalEmulatorControl _terminalEmulatorControl;

    private int _position;

    private bool _isPaused;

    public LogReplayForm(string logText, TerminalEmulatorControl terminalEmulatorControl)
    {
        _logText = logText;
        _terminalEmulatorControl = terminalEmulatorControl;
        InitializeComponent();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        if (!_isPaused && _position < _logText.Length)
        {
            int chunkSize = Math.Min(_logText.Length - _position, trackBar1.Value);
            _terminalEmulatorControl.ProcessOutput(_logText.Substring(_position, chunkSize));
            _position += chunkSize;
        }
    }

    private void button1_Click(object sender, EventArgs e)
    {
        _isPaused = !_isPaused;
        this.button1.Text = (_isPaused ? "Resume" : "Pause");
    }

    private void button2_Click(object sender, EventArgs e)
    {
        _position = 0;
    }
}
