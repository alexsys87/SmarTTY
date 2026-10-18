using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

internal partial class TextHintControl 
{
	private IContainer components = null;

	private ArgumentLabel label1;

	private Panel panel1;

	private Label label2;

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
		label1 = new ArgumentLabel();
		panel1 = new Panel();
		label2 = new Label();
		panel1.SuspendLayout();
		SuspendLayout();
		label1.Anchor = AnchorStyles.None;
		label1.Signature = null;
		label1.BackColor = Color.FromArgb(255, 255, 225);
		label1.Location = new Point(3, 0);
		label1.Name = "label1";
		label1.ActiveIndex = 0;
		label1.Size = new Size(284, 36);
		label1.TabIndex = 0;
		panel1.BackColor = Color.FromArgb(255, 255, 225);
		panel1.BorderStyle = BorderStyle.FixedSingle;
		panel1.Controls.Add(label2);
		panel1.Controls.Add(label1);
		panel1.Dock = DockStyle.Fill;
		panel1.Location = new Point(0, 0);
		panel1.Name = "panel1";
		panel1.Size = new Size(292, 62);
		panel1.TabIndex = 1;
		label2.Anchor = AnchorStyles.None;
		label2.BackColor = Color.FromArgb(255, 255, 225);
		label2.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 204);
		label2.Location = new Point(3, 36);
		label2.Name = "label2";
		label2.Size = new Size(284, 24);
		label2.TabIndex = 0;
		label2.Text = "Press TAB for help on current command";
		label2.TextAlign = ContentAlignment.MiddleLeft;
		Controls.Add(panel1);
		Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 0);
		Name = "TextHintForm";
		Size = new Size(292, 62);
		TabStop = false;
		Text = "TextHintForm";
		Visible = false;
		panel1.ResumeLayout(performLayout: false);
		ResumeLayout(performLayout: false);
	}

}
