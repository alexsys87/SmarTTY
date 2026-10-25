using System;
using System.Windows;
using System.Windows.Threading;
using SmarTTY.Controls;

namespace SmarTTY.GUI;

/// <summary>
/// Replays a recorded log into the live terminal, step by step, with pause and speed controls.
/// </summary>
public partial class LogReplayWindow : Window
{
    private readonly string _logText;

    private readonly byte[] _rawBytes;

    private readonly TerminalEmulatorControl _terminalEmulatorControl;

    // When set, the log is the raw byte stream of a device (COM / raw TCP / telnet) terminal and is replayed through
    // the device terminal (hex dump in HEX mode, decoded text otherwise) instead of the plain text terminal above.
    private readonly DeviceTerminalControl _deviceTerminal;

    private readonly int _length;

    private readonly DispatcherTimer _timer;

    private int _position;

    private bool _isPaused;

    private bool _finished;

    private LogReplayWindow(string logText, TerminalEmulatorControl terminalEmulatorControl)
    {
        InitializeComponent();
        _terminalEmulatorControl = terminalEmulatorControl;
        _logText = logText;
        _length = logText.Length;
        _timer = CreateTimer();
    }

    private LogReplayWindow(byte[] rawBytes, DeviceTerminalControl deviceTerminal)
    {
        InitializeComponent();
        _rawBytes = rawBytes;
        _deviceTerminal = deviceTerminal;
        _length = rawBytes.Length;
        _timer = CreateTimer();
    }

    /// <summary>Replays a recorded text log into <paramref name="terminal"/>.</summary>
    public static void Replay(string logText, TerminalEmulatorControl terminal)
    {
        Show(new LogReplayWindow(logText ?? throw new ArgumentNullException(nameof(logText)), terminal));
    }

    /// <summary>Replays the raw byte log of a device terminal through that same terminal.</summary>
    public static void Replay(byte[] rawBytes, DeviceTerminalControl deviceTerminal)
    {
        Show(new LogReplayWindow(rawBytes ?? throw new ArgumentNullException(nameof(rawBytes)), deviceTerminal ?? throw new ArgumentNullException(nameof(deviceTerminal))));
    }

    private static void Show(LogReplayWindow window)
    {
        WindowOwnership.Attach(window, WindowOwnership.ActiveWindow());
        window.Show();
    }

    private DispatcherTimer CreateTimer()
    {
        DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += OnTimerTick;
        timer.Start();
        return timer;
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        if (_position >= _length)
        {
            if (_length > 0 && !_finished)
            {
                // Playback finished: let the next button click start it from the beginning.
                _finished = true;
                PauseButton.Content = "Replay";
            }
            return;
        }
        if (_isPaused)
        {
            return;
        }
        int chunkSize = Math.Min(_length - _position, Math.Max(1, (int)SpeedSlider.Value));
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

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            ResetPlayback();
            return;
        }
        _isPaused = !_isPaused;
        PauseButton.Content = _isPaused ? "Resume" : "Pause";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
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
        PauseButton.Content = "Pause";
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
