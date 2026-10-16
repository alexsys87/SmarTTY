using System;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Forms;

public partial class DeviceTerminalHostForm : FormBase
{
    private DeviceTerminalSettings _deviceTerminalSettings;

    public DeviceTerminalHostForm(DeviceTerminalSettings deviceTerminalSettings)
    {
        InitializeComponent();
        _deviceTerminalSettings = deviceTerminalSettings;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        this.deviceTerminalControl1.ApplySettings(_deviceTerminalSettings);
    }
}
