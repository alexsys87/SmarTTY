using SmarTTY.Terminal.Input;
using SmarTTY.Controls;
using SmarTTY.GUI.TerminalView;
using SmarTTY.Native;
using SmarTTY.Transport;
using System;
using System.Drawing;
using System.Threading;
using VisualTTY.DeviceTerminal;

using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace SmarTTY.GUI.DevicePanel;

// The serial port's own controls: DTR/RTS/Break, the input modem lines, the line errors reported by the driver, the
// notice about failed writes, and changing the port settings without closing the port.
public partial class WpfDeviceTerminal
{
    private const int LineStatusIntervalMs = 500;

    private const int BreakDurationMs = 250;

    private ToolBarMenuButton _linesButton;

    private MenuItem _dtrItem;

    private MenuItem _rtsItem;

    private MenuItem _breakItem;

    private MenuItem _resetErrorsItem;

    private System.Windows.Controls.TextBlock _lblModemLines;

    // A link in the colour of errors; the text in it is the notice.
    private System.Windows.Controls.Button _lblPortAlert;

    private System.Windows.Controls.TextBlock _lblPortAlertText;

    private System.Windows.Forms.Timer _lineStatusTimer;

    private readonly SerialErrorCounters _lineErrors = new SerialErrorCounters();

    // Something to tell the person about the port (a failed write, a rejected setting); null when there is nothing.
    private string _portProblem;

    // Whether _portProblem comes from a write, which clears itself once a write goes through again.
    private bool _portProblemIsWrite;

    private string _shownModemLines;

    private void InitializeSerialLineControls()
    {
        _dtrItem = new DeviceMenuItem("DTR (data terminal ready)") { IsCheckable = false };
        _dtrItem.Click += (sender, e) => ToggleLine(dtr: true);
        _rtsItem = new DeviceMenuItem("RTS (request to send)") { IsCheckable = false };
        _rtsItem.Click += (sender, e) => ToggleLine(dtr: false);
        _breakItem = new DeviceMenuItem("Send break (" + BreakDurationMs + " ms)");
        _breakItem.Click += OnSendBreakClick;
        _breakItem.Icon = WpfMenus.Icon("event");
        _resetErrorsItem = new DeviceMenuItem("Clear notices and error counters");
        _resetErrorsItem.Icon = WpfMenus.Icon("delete");
        _resetErrorsItem.Click += (sender, e) =>
        {
            _lineErrors.Reset();
            _portProblem = null;
            UpdatePortAlert();
        };

        _linesButton = new ToolBarMenuButton("Lines", "Control lines of the serial port", split: false);
        foreach (System.Windows.Controls.Control item in new System.Windows.Controls.Control[] { _dtrItem, _rtsItem, _breakItem, new Separator(), _resetErrorsItem })
        {
            _linesButton.Items.Add(item);
        }
        _linesButton.Opening += (sender, e) => UpdateSerialLineUi();

        _lblModemLines = new System.Windows.Controls.TextBlock
        {
            Text = "CTS○ DSR○ DCD○ RI○",
            Margin = new System.Windows.Thickness(4, 0, 4, 0),
            ToolTip = "Input lines of the port: a filled dot means the device raises the line.\r\n"
                + "CTS: the device is ready to receive. DSR: the device is present and powered. DCD: carrier. RI: ring."
        };

        _lblPortAlertText = new System.Windows.Controls.TextBlock();
        _lblPortAlert = new System.Windows.Controls.Button { Content = _lblPortAlertText, Margin = new System.Windows.Thickness(4, 0, 4, 0), Visibility = System.Windows.Visibility.Collapsed };
        _lblPortAlert.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "HyperlinkButton");
        _lblPortAlert.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Theme.Error");
        _lblPortAlert.Click += (sender, e) => ShowPortAlertDetails();

        int afterConnection = toolStrip1.Items.IndexOf(lblConnection) + 1;
        toolStrip1.Items.Insert(afterConnection, _lblPortAlert);
        int afterReplay = toolStrip1.Items.IndexOf(btnReplay) + 1;
        toolStrip1.Items.Insert(afterReplay, _linesButton);
        toolStrip1.Items.Insert(afterReplay + 1, _lblModemLines);

        _lineStatusTimer = new System.Windows.Forms.Timer(components)
        {
            Interval = LineStatusIntervalMs
        };
        _lineStatusTimer.Tick += (sender, e) => PollLineStatus();
        UpdateSerialLineUi();
    }

    private SerialPortStream OpenSerialStream()
    {
        return _connectionState == ConnectionStateEnum.Connected ? _asyncTransportConnection?.SerialStream : null;
    }

    /// <summary>Shows the serial controls only for a COM connection and keeps their state in step with the port.</summary>
    private void UpdateSerialLineUi()
    {
        if (_linesButton == null)
        {
            return;
        }
        DeviceTerminalSettings.ConnectionSettings.Serial serial = _deviceTerminalSettings?.Connection as DeviceTerminalSettings.ConnectionSettings.Serial;
        bool isSerial = serial != null;
        _linesButton.SetVisible(isSerial);
        _lblModemLines.SetVisible(isSerial);
        if (!isSerial)
        {
            _lblPortAlert.SetVisible(false);
            _lineStatusTimer.Stop();
            return;
        }

        SerialPortStream stream = OpenSerialStream();
        bool open = stream != null;
        _dtrItem.IsEnabled = open;
        _dtrItem.IsChecked = open ? stream.DtrAsserted : (serial.AdvancedSettings?.DtrOnOpen ?? true);
        bool rtsAutomatic = open && stream.RtsIsAutomatic;
        _rtsItem.IsEnabled = open && !rtsAutomatic;
        _rtsItem.Header = rtsAutomatic ? "RTS (set by hardware flow control)" : "RTS (request to send)";
        _rtsItem.IsChecked = open ? (rtsAutomatic || stream.RtsAsserted) : (serial.AdvancedSettings?.RtsOnOpen ?? true);
        _breakItem.IsEnabled = open && !IsLineClaimed;
        if (open)
        {
            if (!_lineStatusTimer.Enabled)
            {
                _lineStatusTimer.Start();
            }
        }
        else
        {
            _lineStatusTimer.Stop();
            SetModemLinesText(default(SerialModemLines), known: false);
        }
        UpdatePortAlert();
    }

    private void SetModemLinesText(SerialModemLines lines, bool known)
    {
        string text = known ? lines.ToText() : "CTS○ DSR○ DCD○ RI○";
        if (text != _shownModemLines)
        {
            _shownModemLines = text;
            _lblModemLines.Text = text;
        }
    }

    // 1 while a check is running on a worker thread: a driver that blocks in these calls must not pile up checks.
    private int _lineStatusBusy;

    private void PollLineStatus()
    {
        SerialPortStream stream = OpenSerialStream();
        if (stream == null)
        {
            return;
        }
        // The driver calls run on a worker: some virtual and hung drivers block in them, which must not freeze the window.
        if (Interlocked.CompareExchange(ref _lineStatusBusy, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                bool ok = false;
                SerialModemLines lines = default(SerialModemLines);
                uint errorFlags = 0;
                try
                {
                    ok = stream.TryReadLineStatus(out lines, out errorFlags);
                }
                catch (Exception)
                {
                    // Not worth reporting from a periodic check.
                }
                finally
                {
                    Interlocked.Exchange(ref _lineStatusBusy, 0);
                }
                if (!ok)
                {
                    return;
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        // Only for the port that is open now: the result may belong to a connection that has been replaced.
                        if (!ReferenceEquals(OpenSerialStream(), stream))
                        {
                            return;
                        }
                        SetModemLinesText(lines, known: true);
                        if (_lineErrors.Add(errorFlags))
                        {
                            UpdatePortAlert();
                        }
                    }));
                }
                catch (InvalidOperationException)
                {
                    // The terminal was closed meanwhile (this also covers ObjectDisposedException).
                }
            });
        }
        if (_portProblemIsWrite && _portProblem != null && _asyncTransportConnection?.WriteError == null)
        {
            // Writing works again.
            _portProblem = null;
            _portProblemIsWrite = false;
            UpdatePortAlert();
        }
    }

    /// <summary>
    /// Windows announced a COM port (an adapter plugged in again). When it is this tab's port and the connection dropped
    /// on its own, it is opened right away instead of on the next reconnect timer tick.
    /// </summary>
    private void OnSerialPortArrived(string portName)
    {
        if (IsDisposed || !(_deviceTerminalSettings?.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serial)
            || !string.Equals(serial.ComPortName, portName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!_deviceTerminalSettings.ReconnectAutomatically || !CanAutoReconnect)
        {
            return;
        }
        // Automatic: an adapter that appears and disappears again must not wipe the history of failed attempts.
        Connect(resetTerminal: _deviceTerminalSettings.ClearContentsWhenReconnecting, ReopenRetryMs, automatic: true);
    }

    private void OnWriteFailed(object sender, string message)
    {
        _portProblem = message;
        _portProblemIsWrite = true;
        UpdatePortAlert();
    }

    /// <summary>Tells the person about a problem with the port in the toolbar, without a dialog that would pile up.</summary>
    private void ShowPortProblem(string message)
    {
        _portProblem = message;
        _portProblemIsWrite = false;
        UpdatePortAlert();
    }

    private string PortAlertDetails()
    {
        string details = _portProblem ?? string.Empty;
        string errors = _lineErrors.Summary();
        if (errors.Length > 0)
        {
            if (details.Length > 0)
            {
                details += "\r\n\r\n";
            }
            details += "Line errors reported by the driver: " + errors + ".\r\n" + _lineErrors.Hint()
                + "\r\n(Each is counted once per check, every " + LineStatusIntervalMs + " ms, so more characters than shown may have been affected.)";
        }
        return details;
    }

    private void UpdatePortAlert()
    {
        if (_lblPortAlert == null)
        {
            return;
        }
        string errors = _lineErrors.Summary();
        bool isSerial = _deviceTerminalSettings?.Connection is DeviceTerminalSettings.ConnectionSettings.Serial;
        if (!isSerial || (_portProblem == null && errors.Length == 0))
        {
            _lblPortAlert.SetVisible(false);
            return;
        }
        string text;
        if (_portProblem != null)
        {
            string firstLine = _portProblem.Replace("\r", " ").Replace("\n", " ");
            text = firstLine.Length > 60 ? firstLine.Substring(0, 57) + "..." : firstLine;
        }
        else
        {
            text = "Line errors: " + errors;
        }
        _lblPortAlertText.Text = "⚠ " + text;
        _lblPortAlert.ToolTip = PortAlertDetails();
        _lblPortAlert.SetVisible(true);
    }

    private void ShowPortAlertDetails()
    {
        string details = PortAlertDetails();
        if (details.Length > 0)
        {
            UserDialogs.Show(Owner, details, "Serial port", DialogButtons.Ok, DialogIcon.Warning);
        }
    }

    private void ToggleLine(bool dtr)
    {
        SerialPortStream stream = OpenSerialStream();
        if (stream == null)
        {
            return;
        }
        try
        {
            if (dtr)
            {
                stream.SetDtr(!stream.DtrAsserted);
            }
            else
            {
                stream.SetRts(!stream.RtsAsserted);
            }
        }
        catch (Exception exception)
        {
            ShowPortProblem((dtr ? "DTR: " : "RTS: ") + exception.Message);
        }
        UpdateSerialLineUi();
    }

    private void OnSendBreakClick(object sender, EventArgs e)
    {
        SerialPortStream stream = OpenSerialStream();
        if (stream == null || IsLineClaimed)
        {
            return;
        }
        _breakItem.IsEnabled = false;
        ThreadPool.QueueUserWorkItem(state =>
        {
            string error = null;
            try
            {
                stream.SendBreak(BreakDurationMs);
            }
            catch (ObjectDisposedException)
            {
                // The port was closed meanwhile: there is nothing to report.
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (error != null)
                    {
                        ShowPortProblem("Break: " + error);
                    }
                    UpdateSerialLineUi();
                }));
            }
            catch (InvalidOperationException)
            {
                // The terminal was closed meanwhile (this also covers ObjectDisposedException): nobody is left to tell.
            }
        });
    }

    /// <summary>
    /// Puts new serial settings into effect. An open port is reconfigured in place, so the device does not see the port
    /// close and open (which restarts boards that reset on DTR); otherwise the connection is simply opened with them.
    /// Settings the driver rejects are reported and not kept. Returns whether the settings were taken.
    /// A connection the user closed by hand is not opened again unless <paramref name="openWhenManuallyClosed"/>.
    /// </summary>
    private bool TryApplySerialSettings(DeviceTerminalSettings.ConnectionSettings.Serial serial, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings candidate, bool openWhenManuallyClosed = true)
    {
        SerialPortStream stream = OpenSerialStream();
        if (stream != null && candidate != null)
        {
            try
            {
                stream.Reconfigure(candidate);
            }
            catch (Exception exception)
            {
                UserDialogs.Show(Owner, "The port settings were not changed.\r\n\r\n" + exception.Message, "Serial port", DialogButtons.Ok, DialogIcon.Warning);
                return false;
            }
            serial.AdvancedSettings = candidate;
            _transportDisplayName = new SerialTransport(serial).GetDisplayName(includeParameters: true);
            lblConnection.Text = "Connected to " + _transportDisplayName;
            RaiseStatusChanged();
            UpdateSerialLineUi();
            return true;
        }
        serial.AdvancedSettings = candidate;
        if (openWhenManuallyClosed || !_manualDisconnect)
        {
            Connect(resetTerminal: false, 1000);
        }
        else
        {
            // Disconnected by hand: the new speed is kept for the next connection, which the user opens.
            _transportDisplayName = new SerialTransport(serial).GetDisplayName(includeParameters: true);
            RaiseStatusChanged();
        }
        return true;
    }
}
