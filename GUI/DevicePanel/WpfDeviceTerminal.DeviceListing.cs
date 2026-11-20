using SmarTTY.Terminal.Input;
using SmarTTY.Controls;
using SmarTTY.GUI.TerminalView;
using System;
using System.Collections.Generic;
using SmarTTY.Transport;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI.DevicePanel;

// Path completion of the COM/TCP terminal: the names come from the device (VisualTTY/DeviceTerminal/DeviceListingSession.cs runs the listing and holds
// the input typed meanwhile). This part is what the panel does for it: the check of whether a listing can start now, the bytes and their way to the device,
// the timer, and the screen.
public partial class WpfDeviceTerminal : IDeviceListingHost
{
    private DeviceListingSession _listing;
    private Timer _listingTimer;

    private void InitializeDeviceListing()
    {
        _listing = new DeviceListingSession(this);
        _serialTerminalControl.DeviceDirectoryLister = ListDeviceDirectory;
    }

    private bool CanListDeviceDirectory => _asyncTransportConnection != null && !IsLineClaimed && !HexMode && !IsNetconsole
        && _pasteJob == null && _serialTerminalControl.CanListDeviceDirectory;

    private bool ListDeviceDirectory(string directory, Action<IReadOnlyList<DeviceDirectoryEntry>> done)
    {
        return _listing.Start(directory, done);
    }

    /// <summary>Takes the answer of the listing out of received text; returns what is left for the screen.</summary>
    private string FilterDeviceListing(string text)
    {
        return _listing.Filter(text);
    }

    /// <summary>True when typed input was held back because a listing has the line now.</summary>
    private bool HoldInputDuringListing(byte[] buffer)
    {
        return _listing.HoldInput(buffer);
    }

    bool IDeviceListingHost.CanList => CanListDeviceDirectory;

    object IDeviceListingHost.Connection => _asyncTransportConnection;

    int IDeviceListingHost.TickCount => Environment.TickCount;

    byte[] IDeviceListingHost.EncodeCommandLine(string command)
    {
        return _textEncoding.GetBytes(command + (_serialTerminalControl.EnterSequence ?? "\r"));
    }

    bool IDeviceListingHost.Send(byte[] bytes)
    {
        return SendToDevice(bytes, bytes.Length);
    }

    void IDeviceListingHost.StartTimer()
    {
        if (_listingTimer == null)
        {
            _listingTimer = new Timer { Interval = 100 };
            _listingTimer.Tick += (sender, args) => _listing.CheckTimeout();
        }
        _listingTimer.Start();
    }

    void IDeviceListingHost.StopTimer()
    {
        _listingTimer?.Stop();
    }

    void IDeviceListingHost.ShowUnclaimedText(string text)
    {
        if (!IsDisposed)
        {
            _serialTerminalControl.OnDataReceived(text);
        }
    }
}
