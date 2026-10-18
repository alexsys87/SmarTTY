using System.ComponentModel;
using System.Drawing;

namespace SmarTTY.Forms;

public partial class DpiAwareForm 
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
		ClientSize = new Size(284, 262);
		Name = "DpiAwareForm";
		Text = "DpiAwareForm";
		ResumeLayout(performLayout: false);
	}

}
