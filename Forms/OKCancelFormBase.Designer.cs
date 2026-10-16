using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class OKCancelFormBase 
{	private IContainer components = null;

	private Button btnCancel;

	private Button btnOK;

	private Panel panel1;

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
		btnOK = new Button();
		btnCancel = new Button();
		panel1 = new Panel();
		panel1.SuspendLayout();
		SuspendLayout();
		btnOK.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnOK.DialogResult = DialogResult.OK;
		btnOK.Location = new Point(397, 2);
		btnOK.Name = "btnOK";
		btnOK.Size = new Size(75, 23);
		btnOK.TabIndex = 101;
		btnOK.Text = "OK";
		btnOK.UseVisualStyleBackColor = true;
		btnOK.Click += btnOK_Click;
		btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnCancel.DialogResult = DialogResult.Cancel;
		btnCancel.Location = new Point(478, 2);
		btnCancel.Name = "btnCancel";
		btnCancel.Size = new Size(75, 23);
		btnCancel.TabIndex = 102;
		btnCancel.Text = "Cancel";
		btnCancel.UseVisualStyleBackColor = true;
		panel1.Controls.Add(btnCancel);
		panel1.Controls.Add(btnOK);
		panel1.Dock = DockStyle.Bottom;
		panel1.Location = new Point(0, 166);
		panel1.Name = "panel1";
		panel1.Size = new Size(563, 37);
		panel1.TabIndex = 100;
		AcceptButton = btnOK;
		CancelButton = btnCancel;
		ClientSize = new Size(563, 203);
		Controls.Add(panel1);
		Name = "OKCancelFormBase";
		Text = "OKCancelFormBase";
		panel1.ResumeLayout(performLayout: false);
		ResumeLayout(performLayout: false);
	}

}
