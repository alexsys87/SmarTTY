using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class AboutForm 
{
	private IContainer components = null;

	private PictureBox pictureBox1;

	private Label label1;

	private Label label2;

	private Label label4;

	private Button button1;

	private Label label3;

	private LinkLabel linkLabel1;

	private Label label5;

	private Label label6;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(AboutForm));
		label1 = new Label();
		label2 = new Label();
		label4 = new Label();
		button1 = new Button();
		label3 = new Label();
		linkLabel1 = new LinkLabel();
		label5 = new Label();
		label6 = new Label();
		pictureBox1 = new PictureBox();
		((ISupportInitialize)pictureBox1).BeginInit();
		SuspendLayout();
		label1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		label1.Font = new Font("Microsoft Sans Serif", 20.25f, FontStyle.Regular, GraphicsUnit.Point, 0);
		label1.Location = new Point(160, 12);
		label1.Name = "label1";
		label1.Size = new Size(354, 41);
		label1.TabIndex = 1;
		label1.Text = "SmarTTY";
		label1.TextAlign = ContentAlignment.MiddleCenter;
		label2.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		label2.Font = new Font("Microsoft Sans Serif", 9.75f);
		label2.Location = new Point(162, 46);
		label2.Name = "label2";
		label2.Size = new Size(341, 26);
		label2.TabIndex = 2;
		label2.Text = "Version 3.3 [05 May 2022]";
		label2.TextAlign = ContentAlignment.MiddleCenter;
		label4.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		label4.Font = new Font("Microsoft Sans Serif", 12f, FontStyle.Regular, GraphicsUnit.Point, 0);
		label4.Location = new Point(151, 84);
		label4.Name = "label4";
		label4.Size = new Size(364, 28);
		label4.TabIndex = 2;
		label4.Text = "A multi-tabbed SSH client with SCP support.";
		label4.TextAlign = ContentAlignment.MiddleCenter;
		button1.Anchor = AnchorStyles.Bottom;
		button1.DialogResult = DialogResult.Cancel;
		button1.Font = new Font("Microsoft Sans Serif", 12f);
		button1.Location = new Point(194, 259);
		button1.Name = "button1";
		button1.Size = new Size(139, 30);
		button1.TabIndex = 3;
		button1.Text = "Close";
		button1.UseVisualStyleBackColor = true;
		label3.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		label3.Font = new Font("Microsoft Sans Serif", 12f, FontStyle.Regular, GraphicsUnit.Point, 0);
		label3.Location = new Point(31, 140);
		label3.Name = "label3";
		label3.Size = new Size(278, 28);
		label3.TabIndex = 2;
		label3.Text = "Copyright © 2015-2022 Sysprogs OÜ";
		label3.TextAlign = ContentAlignment.MiddleCenter;
		linkLabel1.AutoSize = true;
		linkLabel1.Font = new Font("Microsoft Sans Serif", 12f);
		linkLabel1.Location = new Point(330, 144);
		linkLabel1.Name = "linkLabel1";
		linkLabel1.Size = new Size(150, 20);
		linkLabel1.TabIndex = 4;
		((Label)linkLabel1).TabStop = true;
		linkLabel1.Text = "http://sysprogs.com/";
		linkLabel1.LinkClicked += linkLabel1_LinkClicked;
		label5.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		label5.Font = new Font("Microsoft Sans Serif", 9.75f, FontStyle.Regular, GraphicsUnit.Point, 0);
		label5.Location = new Point(12, 192);
		label5.Name = "label5";
		label5.Size = new Size(502, 62);
		label5.TabIndex = 2;
		label5.Text = "Uses Oxygen icon set (see oxygen.txt)\r\nIncludes an unmodified copy of XMing X11 server (licensed under GPL)";
		label5.TextAlign = ContentAlignment.MiddleCenter;
		label6.Anchor = AnchorStyles.Top;
		label6.BackColor = Color.Transparent;
		label6.Font = new Font("Microsoft Sans Serif", 12f, FontStyle.Regular, GraphicsUnit.Point, 0);
		label6.Location = new Point(141, 166);
		label6.Name = "label6";
		label6.Size = new Size(247, 28);
		label6.TabIndex = 5;
		label6.Text = "All rights reserved";
		label6.TextAlign = ContentAlignment.MiddleCenter;
		pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
		pictureBox1.Location = new Point(12, 12);
		pictureBox1.Name = "pictureBox1";
		pictureBox1.Size = new Size(132, 126);
		pictureBox1.TabIndex = 0;
		pictureBox1.TabStop = false;
		CancelButton = button1;
		ClientSize = new Size(526, 301);
		Controls.Add(linkLabel1);
		Controls.Add(button1);
		Controls.Add(label5);
		Controls.Add(label3);
		Controls.Add(label4);
		Controls.Add(label2);
		Controls.Add(label1);
		Controls.Add(pictureBox1);
		Controls.Add(label6);
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		Name = "AboutForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "About SmarTTY";
		((ISupportInitialize)pictureBox1).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
