using System.ComponentModel;
using System.Drawing;

namespace SmarTTY.Controls;

public partial class TerminalEmulatorBase 
{
	private IContainer components = null;

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
		SuspendLayout();
		AutoScaleDimensions = new SizeF(10f, 18f);
		BackColor = Color.Black;
		base.TerminalMode = true;
		ForeColor = Color.LightGray;
		NavigationLocked = true;
		HideMargins = true;
		base.HorizontalScrollBarVisible = false;
		Name = "TerminalEmulatorControl";
		base.ReadOnly = true;
		ResumeLayout(performLayout: false);
	}

}
