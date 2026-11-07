using System;
using System.Collections.Generic;
using SmarTTY.Transport;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// Path completion of the COM/TCP terminal: the names come from the device. A short command is typed to its shell
// (VisualTTY/DeviceTerminal/DeviceDirectoryListing.cs builds it and takes the answer apart); its echo, its output and
// the prompt after it are kept off the screen, and what the user types meanwhile is held back until the shell is
// at its prompt again, so it is neither swallowed with the answer nor mixed into the listing.
public partial class DeviceTerminalControl
{
    // The listing is given up when the device has not answered in this time (a slow line, a command that hangs).
    private const int DeviceListingTimeoutMs = 4000;

    // After the end of the listing the shell prints its prompt; if it does not look like one, the capture ends anyway.
    private const int DeviceListingPromptWaitMs = 500;

    private DeviceDirectoryCapture _listingCapture;
    private Action<IReadOnlyList<DeviceDirectoryEntry>> _listingDone;
    private AsyncTransportConnection _listingConnection;
    private int _listingStarted;
    private int _listingCompleted;
    private int _listingCounter;
    private string _queuedListingDirectory;
    private Action<IReadOnlyList<DeviceDirectoryEntry>> _queuedListingDone;
    private readonly List<byte[]> _heldInput = new List<byte[]>();
    private Timer _listingTimer;

    private bool IsListingDeviceDirectory => _listingCapture != null;

    private void InitializeDeviceListing()
    {
        _serialTerminalControl.DeviceDirectoryLister = ListDeviceDirectory;
    }

    private bool CanListDeviceDirectory => _asyncTransportConnection != null && !IsLineClaimed && !HexMode && !IsNetconsole
        && _pasteJob == null && _serialTerminalControl.CanListDeviceDirectory;

    private bool ListDeviceDirectory(string directory, Action<IReadOnlyList<DeviceDirectoryEntry>> done)
    {
        if (IsListingDeviceDirectory)
        {
            // One command at a time on the line: the newest request runs when the current one has finished.
            _queuedListingDone?.Invoke(null);
            _queuedListingDirectory = directory;
            _queuedListingDone = done;
            return true;
        }
        if (!CanListDeviceDirectory)
        {
            return false;
        }
        string token = (++_listingCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string command = DeviceDirectoryListing.BuildCommand(directory, token) + (_serialTerminalControl.EnterSequence ?? "\r");
        byte[] bytes = _textEncoding.GetBytes(command);
        _listingCapture = new DeviceDirectoryCapture(token);
        _listingDone = done;
        _listingConnection = _asyncTransportConnection;
        _listingStarted = Environment.TickCount;
        _listingCompleted = 0;
        if (!SendToDevice(bytes, bytes.Length))
        {
            _listingCapture = null;
            _listingDone = null;
            _listingConnection = null;
            return false;
        }
        if (_listingTimer == null)
        {
            _listingTimer = new Timer { Interval = 100 };
            _listingTimer.Tick += (sender, args) => CheckDeviceListingTimeout();
        }
        _listingTimer.Start();
        return true;
    }

    /// <summary>Takes the answer of the listing out of received text; returns what is left for the screen.</summary>
    private string FilterDeviceListing(string text)
    {
        DeviceDirectoryCapture capture = _listingCapture;
        if (capture == null)
        {
            return text;
        }
        string shown = capture.Feed(text);
        if (capture.HasListing && _listingCompleted == 0)
        {
            _listingCompleted = Environment.TickCount | 1;
        }
        if (capture.IsDone)
        {
            FinishDeviceListing(capture.Entries, null);
        }
        return shown;
    }

    private void CheckDeviceListingTimeout()
    {
        DeviceDirectoryCapture capture = _listingCapture;
        if (capture == null)
        {
            _listingTimer?.Stop();
            return;
        }
        int now = Environment.TickCount;
        bool connectionGone = !ReferenceEquals(_listingConnection, _asyncTransportConnection);
        if (capture.HasListing)
        {
            if (connectionGone || now - _listingCompleted >= DeviceListingPromptWaitMs)
            {
                capture.Abort();
                FinishDeviceListing(capture.Entries, null);
            }
        }
        else if (connectionGone || now - _listingStarted >= DeviceListingTimeoutMs)
        {
            // Not a shell after all, or a busy one: its text is shown as it came, nothing is listed.
            FinishDeviceListing(null, capture.Abort());
        }
    }

    private void FinishDeviceListing(IReadOnlyList<DeviceDirectoryEntry> entries, string unclaimedText)
    {
        Action<IReadOnlyList<DeviceDirectoryEntry>> done = _listingDone;
        _listingCapture = null;
        _listingDone = null;
        _listingConnection = null;
        _listingTimer?.Stop();

        if (!string.IsNullOrEmpty(unclaimedText) && !IsDisposed)
        {
            _serialTerminalControl.OnDataReceived(unclaimedText);
        }

        // What was typed while the shell was busy with the listing goes out now, in order.
        if (_heldInput.Count > 0)
        {
            byte[][] held = _heldInput.ToArray();
            _heldInput.Clear();
            if (_asyncTransportConnection != null)
            {
                foreach (byte[] buffer in held)
                {
                    SendToDevice(buffer, buffer.Length);
                }
            }
        }

        done?.Invoke(entries);

        string queuedDirectory = _queuedListingDirectory;
        Action<IReadOnlyList<DeviceDirectoryEntry>> queuedDone = _queuedListingDone;
        _queuedListingDirectory = null;
        _queuedListingDone = null;
        if (queuedDone != null && !ListDeviceDirectory(queuedDirectory, queuedDone))
        {
            queuedDone(null);
        }
    }

    /// <summary>True when typed input was held back because a listing has the line now.</summary>
    private bool HoldInputDuringListing(byte[] buffer)
    {
        if (!IsListingDeviceDirectory)
        {
            return false;
        }
        _heldInput.Add((byte[])buffer.Clone());
        return true;
    }
}
