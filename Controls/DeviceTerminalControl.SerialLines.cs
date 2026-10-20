using SmarTTY.Native;
using SmarTTY.Transport;
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// The serial port's own controls: DTR/RTS/Break, the input modem lines, the line errors reported by the driver, the
// notice about failed writes, and changing the port settings without closing the port.
public partial class DeviceTerminalControl
{
    private const int LineStatusIntervalMs = 500;

    private const int BreakDurationMs = 250;

    private ToolStripDropDownButton _linesButton;

    private ToolStripMenuItem _dtrItem;

    private ToolStripMenuItem _rtsItem;

    private ToolStripMenuItem _breakItem;

    private ToolStripMenuItem _resetErrorsItem;

    private ToolStripLabel _lblModemLines;

    private ToolStripLabel _lblPortAlert;

    private System.Windows.Forms.Timer _lineStatusTimer;

    private readonly SerialErrorCounters _lineErrors = new SerialErrorCounters();

    // Something to tell the person about the port (a failed write, a rejected setting); null when there is nothing.
    private string _portProblem;

    // Whether _portProblem comes from a write, which clears itself once a write goes through again.
    private bool _portProblemIsWrite;

    private string _shownModemLines;

    private void InitializeSerialLineControls()
    {
        _dtrItem = new ToolStripMenuItem("DTR (data terminal ready)") { CheckOnClick = false };
        _dtrItem.Click += (sender, e) => ToggleLine(dtr: true);
        _rtsItem = new ToolStripMenuItem("RTS (request to send)") { CheckOnClick = false };
        _rtsItem.Click += (sender, e) => ToggleLine(dtr: false);
        _breakItem = new ToolStripMenuItem("Send break (" + BreakDurationMs + " ms)");
        _breakItem.Click += OnSendBreakClick;
        _resetErrorsItem = new ToolStripMenuItem("Clear notices and error counters");
        _resetErrorsItem.Click += (sender, e) =>
        {
            _lineErrors.Reset();
            _portProblem = null;
            UpdatePortAlert();
        };

        _linesButton = new ToolStripDropDownButton("Lines")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Control lines of the serial port"
        };
        _linesButton.DropDownItems.AddRange(new ToolStripItem[] { _dtrItem, _rtsItem, _breakItem, new ToolStripSeparator(), _resetErrorsItem });
        _linesButton.DropDownOpening += (sender, e) => UpdateSerialLineUi();

        _lblModemLines = new ToolStripLabel("CTS○ DSR○ DCD○ RI○")
        {
            ToolTipText = "Input lines of the port: a filled dot means the device raises the line.\r\n"
                + "CTS: the device is ready to receive. DSR: the device is present and powered. DCD: carrier. RI: ring."
        };

        _lblPortAlert = new ToolStripLabel
        {
            IsLink = true,
            LinkColor = Color.Firebrick,
            ActiveLinkColor = Color.Firebrick,
            VisitedLinkColor = Color.Firebrick,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Visible = false
        };
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
        _linesButton.Visible = isSerial;
        _lblModemLines.Visible = isSerial;
        if (!isSerial)
        {
            _lblPortAlert.Visible = false;
            _lineStatusTimer.Stop();
            return;
        }

        SerialPortStream stream = OpenSerialStream();
        bool open = stream != null;
        _dtrItem.Enabled = open;
        _dtrItem.Checked = open ? stream.DtrAsserted : (serial.AdvancedSettings?.DtrOnOpen ?? true);
        bool rtsAutomatic = open && stream.RtsIsAutomatic;
        _rtsItem.Enabled = open && !rtsAutomatic;
        _rtsItem.Text = rtsAutomatic ? "RTS (set by hardware flow control)" : "RTS (request to send)";
        _rtsItem.Checked = open ? (rtsAutomatic || stream.RtsAsserted) : (serial.AdvancedSettings?.RtsOnOpen ?? true);
        _breakItem.Enabled = open && !IsLineClaimed;
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

    private void PollLineStatus()
    {
        SerialPortStream stream = OpenSerialStream();
        if (stream == null)
        {
            return;
        }
        if (stream.TryReadLineStatus(out SerialModemLines lines, out uint errorFlags))
        {
            SetModemLinesText(lines, known: true);
            if (_lineErrors.Add(errorFlags))
            {
                UpdatePortAlert();
            }
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
        Connect(resetTerminal: _deviceTerminalSettings.ClearContentsWhenReconnecting, ReopenRetryMs);
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
            _lblPortAlert.Visible = false;
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
        _lblPortAlert.Text = "⚠ " + text;
        _lblPortAlert.ToolTipText = PortAlertDetails();
        _lblPortAlert.Visible = true;
    }

    private void ShowPortAlertDetails()
    {
        string details = PortAlertDetails();
        if (details.Length > 0)
        {
            MessageBox.Show(this, details, "Serial port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        _breakItem.Enabled = false;
        ThreadPool.QueueUserWorkItem(state =>
        {
            string error = null;
            try
            {
                stream.SendBreak(BreakDurationMs);
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
    /// </summary>
    private bool TryApplySerialSettings(DeviceTerminalSettings.ConnectionSettings.Serial serial, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings candidate)
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
                MessageBox.Show(this, "The port settings were not changed.\r\n\r\n" + exception.Message, "Serial port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        Connect(resetTerminal: false, 1000);
        return true;
    }
}
