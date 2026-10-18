using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SmarTTY.Controls;

namespace SmarTTY.Forms;

public partial class DeviceTerminalHostForm 
{
	private IContainer components = null;

	private DeviceTerminalControl deviceTerminalControl1;

	protected override void Dispose(bool disposing)
	{
		if (disposing && components != null)
		{
			components.Dispose();
		}
		base.Dispose(disposing);
	}

	private void InitializeComponent()
	{
		ComponentResourceManager resources = new ComponentResourceManager(typeof(DeviceTerminalHostForm));
		deviceTerminalControl1 = new DeviceTerminalControl();
		SuspendLayout();
		deviceTerminalControl1.Dock = DockStyle.Fill;
		deviceTerminalControl1.HexMode = false;
		deviceTerminalControl1.Location = new Point(0, 0);
		deviceTerminalControl1.Name = "deviceTerminalControl1";
		deviceTerminalControl1.Size = new Size(672, 408);
		deviceTerminalControl1.TabIndex = 0;
		ClientSize = new Size(672, 408);
		Controls.Add(deviceTerminalControl1);
		Icon = (Icon)resources.GetObject("$this.Icon");
		Name = "DeviceTerminalHostForm";
		ShowIcon = true;
		StartPosition = FormStartPosition.CenterScreen;
		Text = "SmarTTY - Raw Terminal";
		ResumeLayout(performLayout: false);
	}

}
