using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SmarTTY.Controls;

namespace SmarTTY.Forms;

public partial class TextHintForm 
{	private IContainer components = null;

	private TextHintControl textHintControl1;

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
		textHintControl1 = new TextHintControl();
		SuspendLayout();
		textHintControl1.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 0);
		textHintControl1.SignatureText = "";
		textHintControl1.Location = new Point(0, 0);
		textHintControl1.Name = "textHintControl1";
		textHintControl1.Description = "Press TAB for help on current command";
		textHintControl1.ShowDescription = true;
		textHintControl1.Size = new Size(260, 56);
		textHintControl1.TabIndex = 0;
		textHintControl1.Text = "textHintControl1";
		textHintControl1.Resize += textHintControl1_Resize;
		AutoScaleDimensions = new SizeF(6f, 13f);
		AutoScaleMode = AutoScaleMode.Font;
		ClientSize = new Size(283, 78);
		Controls.Add(textHintControl1);
		FormBorderStyle = FormBorderStyle.None;
		Name = "TextHintForm";
		ShowIcon = false;
		ShowInTaskbar = true;
		StartPosition = FormStartPosition.Manual;
		Text = "TextHintForm";
		TopMost = true;
		ResumeLayout(performLayout: false);
	}

}
