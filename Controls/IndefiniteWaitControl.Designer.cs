using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class IndefiniteWaitControl 
{
	private IContainer components = null;

	private Button btnCancel;

	private Label lblDescription;

	private PictureBox pictureBox1;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(IndefiniteWaitControl));
		btnCancel = new Button();
		lblDescription = new Label();
		pictureBox1 = new PictureBox();
		((ISupportInitialize)pictureBox1).BeginInit();
		SuspendLayout();
		btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnCancel.Location = new Point(213, 12);
		btnCancel.Margin = new Padding(12);
		btnCancel.Name = "btnCancel";
		btnCancel.Size = new Size(75, 23);
		btnCancel.TabIndex = 1;
		btnCancel.Text = "Cancel";
		btnCancel.UseVisualStyleBackColor = true;
		btnCancel.Click += btnCancel_Click;
		lblDescription.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		lblDescription.Location = new Point(72, 17);
		lblDescription.Margin = new Padding(12, 0, 3, 0);
		lblDescription.Name = "lblDescription";
		lblDescription.Size = new Size(126, 12);
		lblDescription.TabIndex = 2;
		lblDescription.Text = "Waiting to complete...";
		pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
		pictureBox1.Location = new Point(12, 18);
		pictureBox1.Margin = new Padding(12, 3, 3, 3);
		pictureBox1.Name = "pictureBox1";
		pictureBox1.Size = new Size(45, 11);
		pictureBox1.SizeMode = PictureBoxSizeMode.AutoSize;
		pictureBox1.TabIndex = 3;
		pictureBox1.TabStop = false;
		AutoScaleDimensions = new SizeF(6f, 13f);
		Controls.Add(lblDescription);
		Controls.Add(btnCancel);
		MinimumSize = new Size(300, 47);
		Name = "IndefiniteWaitControl";
		Size = new Size(300, 47);
		Paint += IndefiniteWaitControl_Paint;
		((ISupportInitialize)pictureBox1).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
