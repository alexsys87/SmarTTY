using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class PublicKeyManagerForm 
{
	private IContainer components = null;

	private GroupBox gbPublicKey;

	private TextBox txtPublicKey;

	private Label lblPublicKeyDesc;

	private Label lblKeyContainer;

	private Label lblKeyContainerDesc;

	private Button btnResetKey;

	private Button btnImportKey;

	private Button btnExportKey;

	private OpenFileDialog _openFileDialog;

	private SaveFileDialog _saveFileDialog;

	private Button btnClose;

	private PictureBox picPublicKey;

	private PictureBox picRemoteSettings;

	private GroupBox gbRemoteSettings;

	private Label lblRemoteSettingsDesc;

	private Label lblYourPublicKeyDesc;

	private Label lblUserNameDesc;

	private Label lblRemoteHostDesc;

	private Button btnAccept;

	private Button btnStopAccepting;

	private Label txtKeyStatus;

	private Label txtUser;

	private Label txtHost;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(PublicKeyManagerForm));
		gbPublicKey = new GroupBox();
		picPublicKey = new PictureBox();
		btnResetKey = new Button();
		btnImportKey = new Button();
		btnExportKey = new Button();
		txtPublicKey = new TextBox();
		lblKeyContainer = new Label();
		lblKeyContainerDesc = new Label();
		lblPublicKeyDesc = new Label();
		_openFileDialog = new OpenFileDialog();
		_saveFileDialog = new SaveFileDialog();
		btnClose = new Button();
		picRemoteSettings = new PictureBox();
		gbRemoteSettings = new GroupBox();
		btnStopAccepting = new Button();
		btnAccept = new Button();
		lblYourPublicKeyDesc = new Label();
		lblUserNameDesc = new Label();
		txtKeyStatus = new Label();
		txtUser = new Label();
		txtHost = new Label();
		lblRemoteHostDesc = new Label();
		lblRemoteSettingsDesc = new Label();
		gbPublicKey.SuspendLayout();
		((ISupportInitialize)picPublicKey).BeginInit();
		((ISupportInitialize)picRemoteSettings).BeginInit();
		gbRemoteSettings.SuspendLayout();
		SuspendLayout();
		gbPublicKey.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		gbPublicKey.Controls.Add(picPublicKey);
		gbPublicKey.Controls.Add(btnResetKey);
		gbPublicKey.Controls.Add(btnImportKey);
		gbPublicKey.Controls.Add(btnExportKey);
		gbPublicKey.Controls.Add(txtPublicKey);
		gbPublicKey.Controls.Add(lblKeyContainer);
		gbPublicKey.Controls.Add(lblKeyContainerDesc);
		gbPublicKey.Controls.Add(lblPublicKeyDesc);
		gbPublicKey.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 186);
		gbPublicKey.Location = new Point(12, 12);
		gbPublicKey.Name = "gbPublicKey";
		gbPublicKey.Size = new Size(620, 229);
		gbPublicKey.TabIndex = 0;
		gbPublicKey.TabStop = false;
		gbPublicKey.Text = "SmarTTY public key";
		picPublicKey.Image = (Image)resources.GetObject("picPublicKey.Image");
		picPublicKey.Location = new Point(6, 32);
		picPublicKey.Name = "picPublicKey";
		picPublicKey.Size = new Size(128, 128);
		picPublicKey.SizeMode = PictureBoxSizeMode.AutoSize;
		picPublicKey.TabIndex = 2;
		picPublicKey.TabStop = false;
		btnResetKey.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnResetKey.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		btnResetKey.Location = new Point(454, 200);
		btnResetKey.Name = "btnResetKey";
		btnResetKey.Size = new Size(160, 23);
		btnResetKey.TabIndex = 4;
		btnResetKey.Text = "&Reset key";
		btnResetKey.UseVisualStyleBackColor = true;
		btnResetKey.Click += btnResetKey_Click;
		btnImportKey.Anchor = AnchorStyles.Bottom;
		btnImportKey.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		btnImportKey.Location = new Point(236, 200);
		btnImportKey.Name = "btnImportKey";
		btnImportKey.Size = new Size(160, 23);
		btnImportKey.TabIndex = 3;
		btnImportKey.Text = "&Import key";
		btnImportKey.UseVisualStyleBackColor = true;
		btnImportKey.Click += btnImportKey_Click;
		btnExportKey.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		btnExportKey.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		btnExportKey.Location = new Point(6, 200);
		btnExportKey.Name = "btnExportKey";
		btnExportKey.Size = new Size(160, 23);
		btnExportKey.TabIndex = 2;
		btnExportKey.Text = "&Export key";
		btnExportKey.UseVisualStyleBackColor = true;
		btnExportKey.Click += btnExportKey_Click;
		txtPublicKey.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		txtPublicKey.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		txtPublicKey.Location = new Point(140, 32);
		txtPublicKey.Multiline = true;
		txtPublicKey.Name = "txtPublicKey";
		txtPublicKey.ReadOnly = true;
		txtPublicKey.ScrollBars = ScrollBars.Vertical;
		txtPublicKey.Size = new Size(474, 138);
		txtPublicKey.TabIndex = 1;
		lblKeyContainer.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		lblKeyContainer.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		lblKeyContainer.Location = new Point(330, 173);
		lblKeyContainer.Name = "lblKeyContainer";
		lblKeyContainer.Size = new Size(284, 13);
		lblKeyContainer.TabIndex = 0;
		lblKeyContainer.Text = "com";
		lblKeyContainerDesc.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		lblKeyContainerDesc.AutoSize = true;
		lblKeyContainerDesc.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		lblKeyContainerDesc.Location = new Point(6, 173);
		lblKeyContainerDesc.Name = "lblKeyContainerDesc";
		lblKeyContainerDesc.Size = new Size(318, 13);
		lblKeyContainerDesc.TabIndex = 0;
		lblKeyContainerDesc.Text = "The key is stored in the following container under your user profile:";
		lblPublicKeyDesc.AutoSize = true;
		lblPublicKeyDesc.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		lblPublicKeyDesc.Location = new Point(6, 16);
		lblPublicKeyDesc.Name = "lblPublicKeyDesc";
		lblPublicKeyDesc.Size = new Size(553, 13);
		lblPublicKeyDesc.TabIndex = 0;
		lblPublicKeyDesc.Text = "SmarTTY has generated a keypair to automatically logon to SSH servers. Here is the public key in OpenSSH format:";
		_openFileDialog.DefaultExt = "sshkey";
		_openFileDialog.Filter = "SSH keys|*.sshkey";
		_saveFileDialog.DefaultExt = "sshkey";
		_saveFileDialog.Filter = "SSH keys|*.sshkey";
		btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnClose.AutoSize = true;
		btnClose.DialogResult = DialogResult.Cancel;
		btnClose.Location = new Point(557, 427);
		btnClose.Name = "btnClose";
		btnClose.Size = new Size(75, 23);
		btnClose.TabIndex = 7;
		btnClose.Text = "Close";
		btnClose.UseVisualStyleBackColor = true;
		picRemoteSettings.Cursor = Cursors.Hand;
		picRemoteSettings.Image = (Image)resources.GetObject("locked");
		picRemoteSettings.Location = new Point(6, 22);
		picRemoteSettings.Name = "PicRemoteSettings";
		picRemoteSettings.Size = new Size(128, 128);
		picRemoteSettings.SizeMode = PictureBoxSizeMode.AutoSize;
		picRemoteSettings.TabIndex = 2;
		picRemoteSettings.TabStop = false;
		picRemoteSettings.Click += picRemoteSettings_Click;
		gbRemoteSettings.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		gbRemoteSettings.Controls.Add(btnStopAccepting);
		gbRemoteSettings.Controls.Add(btnAccept);
		gbRemoteSettings.Controls.Add(lblYourPublicKeyDesc);
		gbRemoteSettings.Controls.Add(lblUserNameDesc);
		gbRemoteSettings.Controls.Add(txtKeyStatus);
		gbRemoteSettings.Controls.Add(txtUser);
		gbRemoteSettings.Controls.Add(txtHost);
		gbRemoteSettings.Controls.Add(lblRemoteHostDesc);
		gbRemoteSettings.Controls.Add(picRemoteSettings);
		gbRemoteSettings.Controls.Add(lblRemoteSettingsDesc);
		gbRemoteSettings.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 186);
		gbRemoteSettings.Location = new Point(12, 256);
		gbRemoteSettings.Name = "gbRemoteSettings";
		gbRemoteSettings.Size = new Size(620, 165);
		gbRemoteSettings.TabIndex = 3;
		gbRemoteSettings.TabStop = false;
		gbRemoteSettings.Text = "Remote computer settings";
		btnStopAccepting.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnStopAccepting.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		btnStopAccepting.Location = new Point(414, 134);
		btnStopAccepting.Name = "btnStopAccepting";
		btnStopAccepting.Size = new Size(200, 23);
		btnStopAccepting.TabIndex = 6;
		btnStopAccepting.Text = "&Stop accepting your public key";
		btnStopAccepting.UseVisualStyleBackColor = true;
		btnStopAccepting.Click += btnStopAccepting_Click;
		btnAccept.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		btnAccept.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		btnAccept.Location = new Point(140, 134);
		btnAccept.Name = "btnAccept";
		btnAccept.Size = new Size(200, 23);
		btnAccept.TabIndex = 5;
		btnAccept.Text = "&Allow accepting your public key";
		btnAccept.UseVisualStyleBackColor = true;
		btnAccept.Click += btnAccept_Click;
		lblYourPublicKeyDesc.AutoSize = true;
		lblYourPublicKeyDesc.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		lblYourPublicKeyDesc.Location = new Point(140, 110);
		lblYourPublicKeyDesc.Name = "lblYourPublicKeyDesc";
		lblYourPublicKeyDesc.Size = new Size(93, 13);
		lblYourPublicKeyDesc.TabIndex = 3;
		lblYourPublicKeyDesc.Text = "Your public key is:";
		lblUserNameDesc.AutoSize = true;
		lblUserNameDesc.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		lblUserNameDesc.Location = new Point(140, 87);
		lblUserNameDesc.Name = "lblUserNameDesc";
		lblUserNameDesc.Size = new Size(61, 13);
		lblUserNameDesc.TabIndex = 3;
		lblUserNameDesc.Text = "User name:";
		txtKeyStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtKeyStatus.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		txtKeyStatus.Location = new Point(239, 110);
		txtKeyStatus.Name = "txtKeyStatus";
		txtKeyStatus.Size = new Size(375, 13);
		txtKeyStatus.TabIndex = 3;
		txtKeyStatus.Text = "verifying...";
		txtUser.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtUser.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		txtUser.Location = new Point(239, 87);
		txtUser.Name = "txtUser";
		txtUser.Size = new Size(375, 13);
		txtUser.TabIndex = 3;
		txtUser.Text = "hostname";
		txtHost.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtHost.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		txtHost.Location = new Point(239, 65);
		txtHost.Name = "txtHost";
		txtHost.Size = new Size(375, 13);
		txtHost.TabIndex = 3;
		txtHost.Text = "hostname";
		lblRemoteHostDesc.AutoSize = true;
		lblRemoteHostDesc.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		lblRemoteHostDesc.Location = new Point(140, 65);
		lblRemoteHostDesc.Name = "lblRemoteHostDesc";
		lblRemoteHostDesc.Size = new Size(70, 13);
		lblRemoteHostDesc.TabIndex = 3;
		lblRemoteHostDesc.Text = "Remote host:";
		lblRemoteSettingsDesc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		lblRemoteSettingsDesc.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point, 186);
		lblRemoteSettingsDesc.Location = new Point(140, 16);
		lblRemoteSettingsDesc.Name = "lblRemoteSettingsDesc";
		lblRemoteSettingsDesc.Size = new Size(474, 43);
		lblRemoteSettingsDesc.TabIndex = 0;
		lblRemoteSettingsDesc.Text = resources.GetString("lblRemoteSettingsDesc.Text");
		AutoScaleDimensions = new SizeF(6f, 13f);
		CancelButton = btnClose;
		ClientSize = new Size(644, 462);
		Controls.Add(gbRemoteSettings);
		Controls.Add(btnClose);
		Controls.Add(gbPublicKey);
		MaximizeBox = false;
		MinimizeBox = false;
		MinimumSize = new Size(600, 490);
		Name = "PublicKeyManagerForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Manage Public Key";
		gbPublicKey.ResumeLayout(performLayout: false);
		gbPublicKey.PerformLayout();
		((ISupportInitialize)picPublicKey).EndInit();
		((ISupportInitialize)picRemoteSettings).EndInit();
		gbRemoteSettings.ResumeLayout(performLayout: false);
		gbRemoteSettings.PerformLayout();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
