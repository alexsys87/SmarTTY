using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class MinimalisticModeNotificationForm 
{	private IContainer components = null;

	private Button button1;

	private CheckBox checkBox1;

	private Label label1;

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
		button1 = new Button();
		checkBox1 = new CheckBox();
		label1 = new Label();
		SuspendLayout();
		button1.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		button1.DialogResult = DialogResult.OK;
		button1.Location = new Point(228, 69);
		button1.Name = "button1";
		button1.Size = new Size(75, 23);
		button1.TabIndex = 0;
		button1.Text = "OK";
		button1.UseVisualStyleBackColor = true;
		checkBox1.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		checkBox1.AutoSize = true;
		checkBox1.Location = new Point(12, 69);
		checkBox1.Name = "checkBox1";
		checkBox1.Size = new Size(179, 17);
		checkBox1.TabIndex = 1;
		checkBox1.Text = "Do not show this message again";
		checkBox1.UseVisualStyleBackColor = true;
		label1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		label1.Location = new Point(9, 9);
		label1.Name = "label1";
		label1.Size = new Size(294, 57);
		label1.TabIndex = 2;
		label1.Text = "Minimalistic mode activated. \r\nMost of the controls will be hidden.\r\nHover mouse over window edges to unhide controls.";
		AcceptButton = button1;
		ClientSize = new Size(315, 104);
		Controls.Add(label1);
		Controls.Add(checkBox1);
		Controls.Add(button1);
		FormBorderStyle = FormBorderStyle.FixedSingle;
		Name = "MinimalisticModeNotificationForm";
		Text = "Minimalistic mode activated";
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
