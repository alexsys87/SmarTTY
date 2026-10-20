using System.ComponentModel;
using System.Drawing;

namespace SmarTTY.Controls;

public partial class TerminalEmulatorControl 
{
	private IContainer components = null;

	private System.Windows.Forms.Timer _flushTimer;

	protected override void Dispose(bool disposing)
	{
		if (disposing && components != null)
		{
			components.Dispose();
		}
		if (disposing)
		{
			// A log started with "Log all terminal output" would otherwise keep its file open.
			OpenLogFile(null);
		}
		base.Dispose(disposing);
	}

	private void InitializeComponent()
	{
		components = new Container();
		_flushTimer = new System.Windows.Forms.Timer(components);
		SuspendLayout();
		_flushTimer.Enabled = true;
		_flushTimer.Interval = 200;
		_flushTimer.Tick += OnFlushTick;
		AutoScaleDimensions = new SizeF(10f, 18f);
		((ReadonlyTextBrowser)this).HorizontalScrollBarVisible = true;
		Name = "TerminalEmulatorControl";
		ResumeLayout(performLayout: false);
	}

}
