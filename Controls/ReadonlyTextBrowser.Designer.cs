using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class ReadonlyTextBrowser 
{
	private IContainer components = null;

	private System.Windows.Forms.Timer _flashTimer;

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
		_flashTimer = new System.Windows.Forms.Timer(components);
		SuspendLayout();
		_flashTimer.Interval = 1;
		_flashTimer.Tick += OnFlashTick;
		AutoScaleDimensions = new SizeF(10f, 18f);
		AutoScroll = true;
		BackColor = SystemColors.Window;
		Font = new Font("Courier New", 12f, FontStyle.Regular, GraphicsUnit.Point, 204);
		Margin = new Padding(5, 4, 5, 4);
		Name = "ReadonlyTextBrowser";
		Size = new Size(673, 492);
		ResumeLayout(performLayout: false);
	}

}
