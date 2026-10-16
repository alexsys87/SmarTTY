using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class FormBase 
{	private IContainer components = null;

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
		MaximizeBox = false;
		MinimizeBox = false;
		Name = "FormBase";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "FormBase";
		ResumeLayout(performLayout: false);
	}

}
