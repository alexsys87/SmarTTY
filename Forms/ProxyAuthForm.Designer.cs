using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class ProxyAuthForm 
{	private IContainer components = null;

	private Label lblHostNameDesc;

	private Label lblHostName;

	private Label lblUserNameDesc;

	private Label lblPasswordDesc;

	private TextBox txtPassword;

	private PictureBox pictureBox1;

	private Label lblHeading;

	private Button btnOK;

	private Button btnCancel;

	private TextBox txtUserName;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(ProxyAuthForm));
		lblHostNameDesc = new Label();
		lblHostName = new Label();
		lblUserNameDesc = new Label();
		lblPasswordDesc = new Label();
		txtPassword = new TextBox();
		pictureBox1 = new PictureBox();
		lblHeading = new Label();
		btnOK = new Button();
		btnCancel = new Button();
		txtUserName = new TextBox();
		((ISupportInitialize)pictureBox1).BeginInit();
		SuspendLayout();
		lblHostNameDesc.AutoSize = true;
		lblHostNameDesc.Location = new Point(98, 42);
		lblHostNameDesc.Name = "lblHostNameDesc";
		lblHostNameDesc.Size = new Size(40, 13);
		lblHostNameDesc.TabIndex = 0;
		lblHostNameDesc.Text = "Realm:";
		lblHostName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblHostName.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		lblHostName.Location = new Point(165, 42);
		lblHostName.Name = "lblHostName";
		lblHostName.Size = new Size(317, 13);
		lblHostName.TabIndex = 0;
		lblHostName.Text = "localhost";
		lblUserNameDesc.AutoSize = true;
		lblUserNameDesc.Location = new Point(98, 65);
		lblUserNameDesc.Name = "lblUserNameDesc";
		lblUserNameDesc.Size = new Size(61, 13);
		lblUserNameDesc.TabIndex = 0;
		lblUserNameDesc.Text = "User name:";
		lblPasswordDesc.AutoSize = true;
		lblPasswordDesc.Location = new Point(98, 90);
		lblPasswordDesc.Name = "lblPasswordDesc";
		lblPasswordDesc.Size = new Size(56, 13);
		lblPasswordDesc.TabIndex = 0;
		lblPasswordDesc.Text = "Password:";
		txtPassword.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtPassword.Location = new Point(168, 87);
		txtPassword.Name = "txtPassword";
		txtPassword.PasswordChar = '*';
		txtPassword.Size = new Size(314, 20);
		txtPassword.TabIndex = 2;
		pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
		pictureBox1.Location = new Point(12, 37);
		pictureBox1.Name = "pictureBox1";
		pictureBox1.Size = new Size(80, 80);
		pictureBox1.SizeMode = PictureBoxSizeMode.StretchImage;
		pictureBox1.TabIndex = 5;
		pictureBox1.TabStop = false;
		lblHeading.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblHeading.Font = new Font("Microsoft Sans Serif", 14.25f, FontStyle.Regular, GraphicsUnit.Point, 0);
		lblHeading.Location = new Point(12, 9);
		lblHeading.Name = "lblHeading";
		lblHeading.Size = new Size(470, 25);
		lblHeading.TabIndex = 0;
		lblHeading.Text = "Proxy authorization required";
		lblHeading.TextAlign = ContentAlignment.MiddleCenter;
		btnOK.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnOK.DialogResult = DialogResult.OK;
		btnOK.Location = new Point(326, 117);
		btnOK.Name = "btnOK";
		btnOK.Size = new Size(75, 23);
		btnOK.TabIndex = 100;
		btnOK.Text = "&Connect";
		btnOK.UseVisualStyleBackColor = true;
		btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnCancel.DialogResult = DialogResult.Cancel;
		btnCancel.Location = new Point(407, 117);
		btnCancel.Name = "btnCancel";
		btnCancel.Size = new Size(75, 23);
		btnCancel.TabIndex = 101;
		btnCancel.Text = "Cancel";
		btnCancel.UseVisualStyleBackColor = true;
		txtUserName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtUserName.Location = new Point(168, 61);
		txtUserName.Name = "txtUserName";
		txtUserName.Size = new Size(314, 20);
		txtUserName.TabIndex = 1;
		AcceptButton = btnOK;
		AutoScaleDimensions = new SizeF(6f, 13f);
		CancelButton = btnCancel;
		ClientSize = new Size(494, 152);
		Controls.Add(btnCancel);
		Controls.Add(btnOK);
		Controls.Add(pictureBox1);
		Controls.Add(txtUserName);
		Controls.Add(txtPassword);
		Controls.Add(lblHostName);
		Controls.Add(lblPasswordDesc);
		Controls.Add(lblUserNameDesc);
		Controls.Add(lblHeading);
		Controls.Add(lblHostNameDesc);
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		Name = "ProxyAuthForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Enter SSH Password";
		((ISupportInitialize)pictureBox1).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
