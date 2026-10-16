using System.ComponentModel;
using System.Drawing;

namespace SmarTTY.Controls;

public partial class CodeEditorPP 
{
	private IContainer components = null;

	private System.Windows.Forms.Timer _hoverTimer;

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
		_hoverTimer = new System.Windows.Forms.Timer(components);
		SuspendLayout();
		_hoverTimer.Enabled = true;
		_hoverTimer.Tick += OnHoverTimerTick;
		AutoScaleDimensions = new SizeF(10f, 18f);
		Name = "CodeEditorPP";
		ResumeLayout(performLayout: false);
	}

}
