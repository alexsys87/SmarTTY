using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System;

namespace SmarTTY.Forms;

public partial class SCPDownloadForm 
{
	private IContainer components = null;

	private Label lblRemoteFile;

	private TextBox txtRemoteFile;

	private Label lblLocalDir;

	private TextBox txtLocalDir;

	private Button btnDownload;

	private Button btnCancel;

	private Button bntBrowse;

	private FolderBrowserDialog _folderBrowserDialog;

	private Button button1;

	private RemoteOpenFileDialog _remoteFileDialog;

	private PictureBox pictureBox1;

	private CheckBox checkBox1;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(SCPDownloadForm));
		lblRemoteFile = new Label();
		txtRemoteFile = new TextBox();
		lblLocalDir = new Label();
		txtLocalDir = new TextBox();
		btnDownload = new Button();
		btnCancel = new Button();
		bntBrowse = new Button();
		_folderBrowserDialog = new FolderBrowserDialog();
		button1 = new Button();
		_remoteFileDialog = new RemoteOpenFileDialog(components);
		pictureBox1 = new PictureBox();
		checkBox1 = new CheckBox();
		((ISupportInitialize)pictureBox1).BeginInit();
		SuspendLayout();
		lblRemoteFile.AutoSize = true;
		lblRemoteFile.Location = new Point(70, 17);
		lblRemoteFile.Name = "lblRemoteFile";
		lblRemoteFile.Size = new Size(92, 13);
		lblRemoteFile.TabIndex = 0;
		lblRemoteFile.Text = "Remote file name:";
		txtRemoteFile.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtRemoteFile.Location = new Point(168, 14);
		txtRemoteFile.Name = "txtRemoteFile";
		txtRemoteFile.Size = new Size(295, 20);
		txtRemoteFile.TabIndex = 1;
		lblLocalDir.AutoSize = true;
		lblLocalDir.Location = new Point(70, 43);
		lblLocalDir.Name = "lblLocalDir";
		lblLocalDir.Size = new Size(79, 13);
		lblLocalDir.TabIndex = 0;
		lblLocalDir.Text = "Local directory:";
		txtLocalDir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtLocalDir.Location = new Point(168, 40);
		txtLocalDir.Name = "txtLocalDir";
		txtLocalDir.Size = new Size(295, 20);
		txtLocalDir.TabIndex = 3;
		btnDownload.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnDownload.DialogResult = DialogResult.OK;
		btnDownload.Location = new Point(346, 67);
		btnDownload.Name = "btnDownload";
		btnDownload.Size = new Size(75, 23);
		btnDownload.TabIndex = 100;
		btnDownload.Text = "&Download";
		btnDownload.UseVisualStyleBackColor = true;
		btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnCancel.DialogResult = DialogResult.Cancel;
		btnCancel.Location = new Point(427, 67);
		btnCancel.Name = "btnCancel";
		btnCancel.Size = new Size(75, 23);
		btnCancel.TabIndex = 101;
		btnCancel.Text = "Cancel";
		btnCancel.UseVisualStyleBackColor = true;
		bntBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		bntBrowse.Image = (Image)resources.GetObject("bntBrowse.Image");
		bntBrowse.Location = new Point(469, 38);
		bntBrowse.Name = "bntBrowse";
		bntBrowse.Size = new Size(33, 23);
		bntBrowse.TabIndex = 4;
		bntBrowse.UseVisualStyleBackColor = true;
		bntBrowse.Click += bntBrowse_Click;
		_folderBrowserDialog.RootFolder = Environment.SpecialFolder.MyComputer;
		button1.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		button1.Image = (Image)resources.GetObject("button1.Image");
		button1.Location = new Point(469, 12);
		button1.Name = "button1";
		button1.Size = new Size(33, 23);
		button1.TabIndex = 2;
		button1.UseVisualStyleBackColor = true;
		button1.Click += button1_Click;
		_remoteFileDialog.FileName = null;
		_remoteFileDialog.InitialDirectory = null;
		_remoteFileDialog.HostName = null;
		pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
		pictureBox1.Location = new Point(12, 13);
		pictureBox1.Name = "pictureBox1";
		pictureBox1.Size = new Size(52, 47);
		pictureBox1.TabIndex = 102;
		pictureBox1.TabStop = false;
		checkBox1.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		checkBox1.AutoSize = true;
		checkBox1.Location = new Point(12, 71);
		checkBox1.Name = "checkBox1";
		checkBox1.Size = new Size(207, 17);
		checkBox1.TabIndex = 103;
		checkBox1.Text = "Open local directory after downloading";
		checkBox1.UseVisualStyleBackColor = true;
		AcceptButton = btnDownload;
		CancelButton = btnCancel;
		ClientSize = new Size(514, 102);
		Controls.Add(checkBox1);
		Controls.Add(pictureBox1);
		Controls.Add(button1);
		Controls.Add(bntBrowse);
		Controls.Add(btnCancel);
		Controls.Add(btnDownload);
		Controls.Add(txtLocalDir);
		Controls.Add(txtRemoteFile);
		Controls.Add(lblLocalDir);
		Controls.Add(lblRemoteFile);
		MaximizeBox = false;
		MaximumSize = new Size(2000, 140);
		MinimizeBox = false;
		MinimumSize = new Size(300, 140);
		Name = "SCPDownloadForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Download a File with SCP";
		((ISupportInitialize)pictureBox1).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
