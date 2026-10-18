using System.ComponentModel;

namespace SmarTTY.Controls;

public partial class DpiAwareControl 
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
		Name = "DpiAwareControl";
		ResumeLayout(performLayout: false);
	}

}
