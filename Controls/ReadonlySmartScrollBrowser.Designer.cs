using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class ReadonlySmartScrollBrowser 
{
	private IContainer components = null;

	private Timer _timer;

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
		components = new Container();
		_timer = new Timer(components);
		SuspendLayout();
		_timer.Interval = 60;
		_timer.Tick += OnTimerTick;
		AutoScaleDimensions = new SizeF(10f, 18f);
		Name = "ReadonlySmartScrollBrowser";
		ResumeLayout(performLayout: false);
	}

}
