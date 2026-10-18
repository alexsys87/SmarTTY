using System.ComponentModel;

namespace SmarTTY.Controls;

public partial class DoubleBufferedListView 
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
		components = new Container();
	}

}
