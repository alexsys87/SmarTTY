using System.ComponentModel;
using System.Windows.Threading;

namespace SmarTTY;

public partial class SshConnector 
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
		components = new Container();
	}

}
