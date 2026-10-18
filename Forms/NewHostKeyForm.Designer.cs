using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class NewHostKeyForm 
{
	private IContainer components = null;

	private Label lblHostNameDesc;

	private Label lblHostName;

	private PictureBox pictureBox1;

	private Label lblHeading;

	private Button btnOK;

	private Button btnCancel;

	private Label lblIntro;

	private Label lblKeyDesc;

	private Label lblKey;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(NewHostKeyForm));
		lblHostNameDesc = new Label();
		lblHostName = new Label();
		pictureBox1 = new PictureBox();
		lblHeading = new Label();
		btnOK = new Button();
		btnCancel = new Button();
		lblIntro = new Label();
		lblKeyDesc = new Label();
		lblKey = new Label();
		((ISupportInitialize)pictureBox1).BeginInit();
		SuspendLayout();
		lblHostNameDesc.AutoSize = true;
		lblHostNameDesc.Location = new Point(98, 79);
		lblHostNameDesc.Name = "lblHostNameDesc";
		lblHostNameDesc.Size = new Size(61, 13);
		lblHostNameDesc.TabIndex = 0;
		lblHostNameDesc.Text = "Host name:";
		lblHostName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblHostName.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		lblHostName.Location = new Point(181, 79);
		lblHostName.Name = "lblHostName";
		lblHostName.Size = new Size(301, 13);
		lblHostName.TabIndex = 0;
		lblHostName.Text = "localhost";
		pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
		pictureBox1.Location = new Point(12, 37);
		pictureBox1.Name = "pictureBox1";
		pictureBox1.Size = new Size(80, 80);
		pictureBox1.SizeMode = PictureBoxSizeMode.CenterImage;
		pictureBox1.TabIndex = 5;
		pictureBox1.TabStop = false;
		lblHeading.Font = new Font("Microsoft Sans Serif", 14.25f, FontStyle.Regular, GraphicsUnit.Point, 0);
		lblHeading.Location = new Point(12, 9);
		lblHeading.Name = "lblHeading";
		lblHeading.Size = new Size(470, 25);
		lblHeading.TabIndex = 0;
		lblHeading.Text = "Save host key";
		lblHeading.TextAlign = ContentAlignment.MiddleCenter;
		btnOK.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnOK.DialogResult = DialogResult.OK;
		btnOK.Location = new Point(326, 117);
		btnOK.Name = "btnOK";
		btnOK.Size = new Size(75, 23);
		btnOK.TabIndex = 1;
		btnOK.Text = "&Save";
		btnOK.UseVisualStyleBackColor = true;
		btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnCancel.DialogResult = DialogResult.Cancel;
		btnCancel.Location = new Point(407, 117);
		btnCancel.Name = "btnCancel";
		btnCancel.Size = new Size(75, 23);
		btnCancel.TabIndex = 2;
		btnCancel.Text = "Cancel";
		btnCancel.UseVisualStyleBackColor = true;
		lblIntro.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblIntro.Location = new Point(98, 42);
		lblIntro.Name = "lblIntro";
		lblIntro.Size = new Size(384, 37);
		lblIntro.TabIndex = 6;
		lblIntro.Text = "SmarTTY is connecting to this host for the first time. It is recommended to save the host key locally to detect if the host is compromised in the future.";
		lblKeyDesc.AutoSize = true;
		lblKeyDesc.Location = new Point(98, 98);
		lblKeyDesc.Name = "lblKeyDesc";
		lblKeyDesc.Size = new Size(77, 13);
		lblKeyDesc.TabIndex = 0;
		lblKeyDesc.Text = "Key fingerprint:";
		lblKey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblKey.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		lblKey.Location = new Point(181, 98);
		lblKey.Name = "lblKey";
		lblKey.Size = new Size(301, 13);
		lblKey.TabIndex = 0;
		lblKey.Text = "00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00";
		AcceptButton = btnOK;
		CancelButton = btnCancel;
		ClientSize = new Size(494, 152);
		Controls.Add(lblIntro);
		Controls.Add(btnCancel);
		Controls.Add(btnOK);
		Controls.Add(pictureBox1);
		Controls.Add(lblKey);
		Controls.Add(lblHostName);
		Controls.Add(lblHeading);
		Controls.Add(lblKeyDesc);
		Controls.Add(lblHostNameDesc);
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		Name = "NewHostKeyForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Save Host Key";
		((ISupportInitialize)pictureBox1).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
