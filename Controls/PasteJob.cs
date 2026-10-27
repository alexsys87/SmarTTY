using SmarTTY.Terminal;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

/// <summary>
/// A paste that is sent in several pieces without blocking the window: one piece at a time, the next one only after the
/// previous one has been written, with the pauses of a slow paste and, if wanted, after the echo of the line or the prompt
/// of the device has arrived. A paste that takes more than a moment shows a progress window with Cancel; Esc in the
/// terminal cancels it as well. Everything except <c>written</c> callbacks runs on the UI thread.
/// </summary>
internal sealed class PasteJob
{
    public enum Outcome
    {
        Completed,
        Cancelled,
        Failed
    }

    /// <summary>
    /// Sends one piece (on the UI thread) and calls <c>written</c> - on any thread, once - when it has been written. False when
    /// the piece cannot be sent (the connection is closed): <c>written</c> is not called then.
    /// </summary>
    public delegate bool SendPiece(string text, Action written);

    private enum State
    {
        Writing,
        Pausing,
        WaitingOutput,
        Done
    }

    // The progress window appears only for a paste that takes longer than this.
    private const int ProgressDelayMs = 400;

    private const int ReceivedLimit = 4096;

    private readonly Control _owner;

    private readonly string _title;

    private readonly List<PasteStep> _steps;

    private readonly SendPiece _send;

    private readonly Action<Outcome, string> _finished;

    private readonly int _charDelayMs;

    private readonly int _lineDelayMs;

    private readonly PasteSettings.SlowWaitMode _waitMode;

    private readonly string _prompt;

    private readonly int _waitTimeoutMs;

    private readonly int _totalCharacters;

    private readonly Timer _timer = new Timer();

    private readonly StringBuilder _received = new StringBuilder();

    private State _state = State.Pausing;

    private int _index;

    private int _sentCharacters;

    private int _startTick;

    private int _waitStartTick;

    private string _expectedEcho;

    private IPasteProgress _form;

    public bool IsActive => _state != State.Done;

    /// <param name="slow">
    /// Use the pauses and waits of <paramref name="settings"/>; without it the pieces follow each other as soon as they are
    /// written.
    /// </param>
    public PasteJob(Control owner, string title, List<PasteStep> steps, SendPiece send, PasteSettings settings, bool slow, Action<Outcome, string> finished)
    {
        _owner = owner;
        _title = title;
        _steps = steps;
        _send = send;
        _finished = finished;
        _totalCharacters = PasteModes.TotalLength(steps);
        if (slow && settings != null)
        {
            _charDelayMs = Math.Max(settings.SlowCharDelayMs, 0);
            _lineDelayMs = Math.Max(settings.SlowLineDelayMs, 0);
            _waitMode = settings.SlowWait;
            _prompt = (settings.SlowPrompt ?? "").Trim();
            _waitTimeoutMs = Math.Max(settings.SlowWaitTimeoutMs, 100);
            if (_waitMode == PasteSettings.SlowWaitMode.Prompt && _prompt.Length == 0)
            {
                _waitMode = PasteSettings.SlowWaitMode.None;
            }
        }
        _timer.Tick += OnTimer;
    }

    public void Start()
    {
        _startTick = Environment.TickCount;
        Next();
    }

    /// <summary>Stops the paste; what was sent stays sent.</summary>
    public void Cancel()
    {
        Finish(Outcome.Cancelled, null);
    }

    /// <summary>Stops the paste without telling anybody (the terminal is closing).</summary>
    public void Abort()
    {
        if (_state == State.Done)
        {
            return;
        }
        _state = State.Done;
        Cleanup();
    }

    /// <summary>The text the device (or the shell) sent meanwhile: the echo and the prompt are looked for in it.</summary>
    public void OnOutput(string text)
    {
        if ((_state != State.Writing && _state != State.WaitingOutput) || string.IsNullOrEmpty(text))
        {
            return;
        }
        _received.Append(text);
        if (_received.Length > ReceivedLimit)
        {
            _received.Remove(0, _received.Length - ReceivedLimit);
        }
        if (_state == State.WaitingOutput && IsSatisfied())
        {
            _timer.Stop();
            _state = State.Pausing;
            // Not from inside the processing of the output.
            Post(ContinueAfterLine);
        }
    }

    private void Next()
    {
        if (_state == State.Done)
        {
            return;
        }
        if (_index >= _steps.Count)
        {
            Finish(Outcome.Completed, null);
            return;
        }
        _state = State.Writing;
        // The answer to this piece may arrive before it is known to be written.
        _received.Clear();
        bool accepted;
        try
        {
            accepted = _send(_steps[_index].Text, OnWritten);
        }
        catch (Exception exception)
        {
            Finish(Outcome.Failed, exception.Message);
            return;
        }
        if (!accepted)
        {
            Finish(Outcome.Failed, "The connection is closed.");
        }
    }

    // Any thread.
    private void OnWritten()
    {
        Post(AfterWrite);
    }

    private void Post(Action action)
    {
        try
        {
            if (!_owner.IsDisposed && _owner.IsHandleCreated)
            {
                _owner.BeginInvoke(action);
            }
        }
        catch (InvalidOperationException)
        {
            // The terminal is being closed.
        }
    }

    private void AfterWrite()
    {
        if (_state != State.Writing)
        {
            return;
        }
        PasteStep step = _steps[_index];
        _index++;
        _sentCharacters += step.Text.Length;
        UpdateProgress();
        if (_index >= _steps.Count)
        {
            Finish(Outcome.Completed, null);
            return;
        }
        if (step.EndsLine && _waitMode != PasteSettings.SlowWaitMode.None)
        {
            _expectedEcho = _waitMode == PasteSettings.SlowWaitMode.Echo ? StripControl(step.Text).Trim() : null;
            if (_waitMode == PasteSettings.SlowWaitMode.Prompt || !string.IsNullOrEmpty(_expectedEcho))
            {
                _waitStartTick = Environment.TickCount;
                _state = State.WaitingOutput;
                if (IsSatisfied())
                {
                    _state = State.Pausing;
                    ContinueAfterLine();
                    return;
                }
                _timer.Interval = 50;
                _timer.Start();
                return;
            }
        }
        Pause(step.EndsLine ? _lineDelayMs : _charDelayMs);
    }

    private void ContinueAfterLine()
    {
        if (_state != State.Pausing)
        {
            return;
        }
        Pause(_lineDelayMs);
    }

    private void Pause(int delayMs)
    {
        if (delayMs <= 0)
        {
            Next();
            return;
        }
        _state = State.Pausing;
        _timer.Interval = delayMs;
        _timer.Start();
    }

    private void OnTimer(object sender, EventArgs e)
    {
        _timer.Stop();
        UpdateProgress();
        if (_state == State.Pausing)
        {
            Next();
        }
        else if (_state == State.WaitingOutput)
        {
            if (unchecked(Environment.TickCount - _waitStartTick) < _waitTimeoutMs)
            {
                _timer.Start();
            }
            else if (_waitMode == PasteSettings.SlowWaitMode.Prompt)
            {
                Finish(Outcome.Failed, "The prompt \"" + _prompt + "\" did not come within " + (_waitTimeoutMs / 1000.0).ToString("0.#") + " s.");
            }
            else
            {
                // No echo (the device does not echo, or echoes differently): the pauses are all there is.
                _state = State.Pausing;
                Pause(_lineDelayMs);
            }
        }
    }

    private bool IsSatisfied()
    {
        string received = StripControl(_received.ToString());
        if (_waitMode == PasteSettings.SlowWaitMode.Prompt)
        {
            return received.TrimEnd().EndsWith(_prompt, StringComparison.Ordinal);
        }
        return !string.IsNullOrEmpty(_expectedEcho) && received.Contains(_expectedEcho);
    }

    private static string StripControl(string text)
    {
        return PasteSanitizer.RemoveControlCharacters(text);
    }

    private void UpdateProgress()
    {
        if (_state == State.Done)
        {
            return;
        }
        if (_form == null && unchecked(Environment.TickCount - _startTick) > ProgressDelayMs)
        {
            _form = ThemedPasteProgress.Create(_title);
            _form.CancelRequested += (object sender, EventArgs e) => Cancel();
            _form.ShowNear(_owner);
        }
        _form?.SetProgress(_sentCharacters, _totalCharacters, Describe());
    }

    private string Describe()
    {
        if (_totalCharacters >= 4096)
        {
            return (_sentCharacters / 1024) + " of " + (_totalCharacters / 1024) + " KB sent";
        }
        return _sentCharacters + " of " + _totalCharacters + " characters sent";
    }

    private void Finish(Outcome outcome, string message)
    {
        if (_state == State.Done)
        {
            return;
        }
        _state = State.Done;
        Cleanup();
        _finished?.Invoke(outcome, message);
    }

    private void Cleanup()
    {
        _timer.Stop();
        _timer.Dispose();
        IPasteProgress form = _form;
        _form = null;
        try
        {
            form?.Close();
            form?.Dispose();
        }
        catch
        {
        }
    }
}
