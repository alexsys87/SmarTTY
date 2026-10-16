using SmarTTY.Controls;
using System;

namespace SmarTTY.Forms;

public partial class LogReplayForm : DpiAwareForm
{
    private string _logText;

    private TerminalEmulatorControl _terminalEmulatorControl;

    private int _position;

    private bool _isPaused;

    public LogReplayForm(string text, TerminalEmulatorControl terminalEmulatorControl)
    {
        _logText = text;
        _terminalEmulatorControl = terminalEmulatorControl;
        InitializeComponent();
    }

    private void OnTimerTick(object sender, EventArgs e2)
    {
        if (!_isPaused && _position < _logText.Length)
        {
            int num = Math.Min(_logText.Length - _position, trackBar1.Value);
            _terminalEmulatorControl.ProcessOutput(_logText.Substring(_position, num));
            _position += num;
        }
    }

    private void button1_Click(object sender, EventArgs e2)
    {
        _isPaused = !_isPaused;
        this.button1.Text = (_isPaused ? "Resume" : "Pause");
    }

    private void button2_Click(object sender, EventArgs e)
    {
        _position = 0;
    }
}
