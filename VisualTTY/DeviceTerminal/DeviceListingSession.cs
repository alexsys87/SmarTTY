using System;
using System.Collections.Generic;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// What <see cref="DeviceListingSession"/> needs of the terminal panel: whether a listing can be started now, which connection is the current one (it is
/// compared by reference, a reconnect gives another), the bytes of a command line for the device, sending bytes, the timer that watches the answer and
/// the screen that gets the text of a device that did not answer like a shell. Every member is something the panel did itself before.
/// </summary>
public interface IDeviceListingHost
{
    /// <summary>A listing can be started now (a connection, a line that is not used by anything else, a shell at its prompt).</summary>
    bool CanList { get; }

    /// <summary>The current connection of the panel, or null when there is none.</summary>
    object Connection { get; }

    /// <summary>The milliseconds of the system clock (it wraps around, as Environment.TickCount).</summary>
    int TickCount { get; }

    /// <summary>The bytes that send <paramref name="command"/> to the shell and run it (the Enter of the connection is added, in the encoding of the panel).</summary>
    byte[] EncodeCommandLine(string command);

    /// <summary>Sends bytes to the device; false when they could not be sent.</summary>
    bool Send(byte[] bytes);

    /// <summary>Starts the timer that calls <see cref="DeviceListingSession.CheckTimeout"/> every 100 ms.</summary>
    void StartTimer();

    void StopTimer();

    /// <summary>Shows text that the listing took from the device and that was not its answer.</summary>
    void ShowUnclaimedText(string text);
}

/// <summary>
/// Path completion of the COM/TCP terminal: the names come from the device. A short command is typed to its shell
/// (<see cref="DeviceDirectoryListing"/> builds it and takes the answer apart); its echo, its output and the prompt after it are kept off the screen,
/// and what the user types meanwhile is held back until the shell is at its prompt again, so it is neither swallowed with the answer nor mixed into
/// the listing. One command runs at a time: the newest request waits for the current one and takes the place of an older waiting request.
/// </summary>
public sealed class DeviceListingSession
{
    /// <summary>The listing is given up when the device has not answered in this time (a slow line, a command that hangs).</summary>
    public const int TimeoutMs = 4000;

    /// <summary>After the end of the listing the shell prints its prompt; if it does not look like one, the capture ends anyway.</summary>
    public const int PromptWaitMs = 500;

    private readonly IDeviceListingHost _host;
    private DeviceDirectoryCapture _capture;
    private Action<IReadOnlyList<DeviceDirectoryEntry>> _done;
    private object _connection;
    private int _started;
    private int _completed;
    private int _counter;
    private string _queuedDirectory;
    private Action<IReadOnlyList<DeviceDirectoryEntry>> _queuedDone;
    private readonly List<byte[]> _heldInput = new List<byte[]>();

    public DeviceListingSession(IDeviceListingHost host)
    {
        _host = host;
    }

    /// <summary>A listing has the line now.</summary>
    public bool IsListing => _capture != null;

    /// <summary>
    /// Asks for the names in <paramref name="directory"/>; <paramref name="done"/> gets them, or null when nothing could be listed. False when no listing
    /// can be started now (and <paramref name="done"/> is not called); true when it was started or is waiting for the one that runs.
    /// </summary>
    public bool Start(string directory, Action<IReadOnlyList<DeviceDirectoryEntry>> done)
    {
        if (IsListing)
        {
            // One command at a time on the line: the newest request runs when the current one has finished.
            _queuedDone?.Invoke(null);
            _queuedDirectory = directory;
            _queuedDone = done;
            return true;
        }
        if (!_host.CanList)
        {
            return false;
        }
        string token = (++_counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] bytes = _host.EncodeCommandLine(DeviceDirectoryListing.BuildCommand(directory, token));
        _capture = new DeviceDirectoryCapture(token);
        _done = done;
        _connection = _host.Connection;
        _started = _host.TickCount;
        _completed = 0;
        if (!_host.Send(bytes))
        {
            _capture = null;
            _done = null;
            _connection = null;
            return false;
        }
        _host.StartTimer();
        return true;
    }

    /// <summary>Takes the answer of the listing out of received text; returns what is left for the screen.</summary>
    public string Filter(string text)
    {
        DeviceDirectoryCapture capture = _capture;
        if (capture == null)
        {
            return text;
        }
        string shown = capture.Feed(text);
        if (capture.HasListing && _completed == 0)
        {
            _completed = _host.TickCount | 1;
        }
        if (capture.IsDone)
        {
            Finish(capture.Entries, null);
        }
        return shown;
    }

    /// <summary>Called by the timer: gives the listing up when the device was too slow or the connection is gone.</summary>
    public void CheckTimeout()
    {
        DeviceDirectoryCapture capture = _capture;
        if (capture == null)
        {
            _host.StopTimer();
            return;
        }
        int now = _host.TickCount;
        bool connectionGone = !ReferenceEquals(_connection, _host.Connection);
        if (capture.HasListing)
        {
            if (connectionGone || now - _completed >= PromptWaitMs)
            {
                capture.Abort();
                Finish(capture.Entries, null);
            }
        }
        else if (connectionGone || now - _started >= TimeoutMs)
        {
            // Not a shell after all, or a busy one: its text is shown as it came, nothing is listed.
            Finish(null, capture.Abort());
        }
    }

    private void Finish(IReadOnlyList<DeviceDirectoryEntry> entries, string unclaimedText)
    {
        Action<IReadOnlyList<DeviceDirectoryEntry>> done = _done;
        _capture = null;
        _done = null;
        _connection = null;
        _host.StopTimer();

        if (!string.IsNullOrEmpty(unclaimedText))
        {
            _host.ShowUnclaimedText(unclaimedText);
        }

        // What was typed while the shell was busy with the listing goes out now, in order.
        if (_heldInput.Count > 0)
        {
            byte[][] held = _heldInput.ToArray();
            _heldInput.Clear();
            if (_host.Connection != null)
            {
                foreach (byte[] buffer in held)
                {
                    _host.Send(buffer);
                }
            }
        }

        done?.Invoke(entries);

        string queuedDirectory = _queuedDirectory;
        Action<IReadOnlyList<DeviceDirectoryEntry>> queuedDone = _queuedDone;
        _queuedDirectory = null;
        _queuedDone = null;
        if (queuedDone != null && !Start(queuedDirectory, queuedDone))
        {
            queuedDone(null);
        }
    }

    /// <summary>True when typed input was held back because a listing has the line now.</summary>
    public bool HoldInput(byte[] buffer)
    {
        if (!IsListing)
        {
            return false;
        }
        _heldInput.Add((byte[])buffer.Clone());
        return true;
    }
}
