using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System;

namespace SmarTTY.Forms;

public partial class DirectoryTransferForm 
{
	private IContainer components = null;

	private Label lblRemoteFile;

	private TextBox txtRemoteDir;

	private Label lblLocalDir;

	private TextBox txtLocalDir;

	private Button btnDownload;

	private Button btnCancel;

	private Button bntBrowse;

	private FolderBrowserDialog _folderBrowserDialog;

	private Label label1;

	private ProgressBar progressBar1;

	private Label lblStats;

	private Button btnRemoteBrowse;

	private RemoteFolderDialog _remoteFolderDialog;

	private PictureBox pictureBox1;

	private RadioButton rbSCP;

	private RadioButton rbTar;

	private PictureBox pictureBox2;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(DirectoryTransferForm));
		lblRemoteFile = new Label();
		txtRemoteDir = new TextBox();
		lblLocalDir = new Label();
		txtLocalDir = new TextBox();
		btnDownload = new Button();
		btnCancel = new Button();
		bntBrowse = new Button();
		_folderBrowserDialog = new FolderBrowserDialog();
		label1 = new Label();
		progressBar1 = new ProgressBar();
		lblStats = new Label();
		btnRemoteBrowse = new Button();
		_remoteFolderDialog = new RemoteFolderDialog(components);
		pictureBox1 = new PictureBox();
		rbSCP = new RadioButton();
		rbTar = new RadioButton();
		pictureBox2 = new PictureBox();
		((ISupportInitialize)pictureBox1).BeginInit();
		((ISupportInitialize)pictureBox2).BeginInit();
		SuspendLayout();
		lblRemoteFile.AutoSize = true;
		lblRemoteFile.Location = new Point(70, 17);
		lblRemoteFile.Name = "lblRemoteFile";
		lblRemoteFile.Size = new Size(90, 13);
		lblRemoteFile.TabIndex = 0;
		lblRemoteFile.Text = "Remote directory:";
		txtRemoteDir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtRemoteDir.Location = new Point(168, 14);
		txtRemoteDir.Name = "txtRemoteDir";
		txtRemoteDir.Size = new Size(295, 20);
		txtRemoteDir.TabIndex = 1;
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
		btnDownload.Location = new Point(346, 108);
		btnDownload.Name = "btnDownload";
		btnDownload.Size = new Size(75, 23);
		btnDownload.TabIndex = 7;
		btnDownload.Text = "&Download";
		btnDownload.UseVisualStyleBackColor = true;
		btnDownload.Click += btnDownload_Click;
		btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnCancel.DialogResult = DialogResult.Cancel;
		btnCancel.Location = new Point(427, 108);
		btnCancel.Name = "btnCancel";
		btnCancel.Size = new Size(75, 23);
		btnCancel.TabIndex = 8;
		btnCancel.Text = "Cancel";
		btnCancel.UseVisualStyleBackColor = true;
		btnCancel.Click += btnCancel_Click;
		bntBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		bntBrowse.Image = (Image)resources.GetObject("bntBrowse.Image");
		bntBrowse.Location = new Point(469, 38);
		bntBrowse.Name = "bntBrowse";
		bntBrowse.Size = new Size(33, 23);
		bntBrowse.TabIndex = 4;
		bntBrowse.UseVisualStyleBackColor = true;
		bntBrowse.Click += bntBrowse_Click;
		_folderBrowserDialog.RootFolder = Environment.SpecialFolder.MyComputer;
		label1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		label1.AutoEllipsis = true;
		label1.Location = new Point(12, 89);
		label1.Name = "label1";
		label1.Size = new Size(409, 13);
		label1.TabIndex = 6;
		label1.Text = "Inactive";
		progressBar1.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		progressBar1.Location = new Point(12, 108);
		progressBar1.Maximum = 1000;
		progressBar1.Name = "progressBar1";
		progressBar1.Size = new Size(328, 23);
		progressBar1.TabIndex = 7;
		progressBar1.Visible = false;
		lblStats.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		lblStats.AutoEllipsis = true;
		lblStats.Location = new Point(427, 89);
		lblStats.Name = "lblStats";
		lblStats.Size = new Size(75, 13);
		lblStats.TabIndex = 6;
		btnRemoteBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		btnRemoteBrowse.Image = (Image)resources.GetObject("btnRemoteBrowse.Image");
		btnRemoteBrowse.Location = new Point(469, 12);
		btnRemoteBrowse.Name = "btnRemoteBrowse";
		btnRemoteBrowse.Size = new Size(33, 23);
		btnRemoteBrowse.TabIndex = 2;
		btnRemoteBrowse.UseVisualStyleBackColor = true;
		btnRemoteBrowse.Click += btnRemoteBrowse_Click;
		_remoteFolderDialog.InitialFolder = null;
		_remoteFolderDialog.HostName = null;
		_remoteFolderDialog.CommandRequested += StartRemoteCommand;
		pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
		pictureBox1.Location = new Point(12, 13);
		pictureBox1.Name = "pictureBox1";
		pictureBox1.Size = new Size(52, 47);
		pictureBox1.TabIndex = 9;
		pictureBox1.TabStop = false;
		rbSCP.AutoSize = true;
		rbSCP.Location = new Point(194, 66);
		rbSCP.Name = "rbSCP";
		rbSCP.Size = new Size(158, 17);
		rbSCP.TabIndex = 6;
		rbSCP.Text = "Invoke SCP file-by-file (slow)";
		rbSCP.UseVisualStyleBackColor = true;
		rbTar.AutoSize = true;
		rbTar.Checked = true;
		rbTar.Location = new Point(73, 66);
		rbTar.Name = "rbTar";
		rbTar.Size = new Size(115, 17);
		rbTar.TabIndex = 5;
		rbTar.TabStop = true;
		rbTar.Text = "Use on-the-fly TAR";
		rbTar.UseVisualStyleBackColor = true;
		pictureBox2.Image = (Image)resources.GetObject("pictureBox2.Image");
		pictureBox2.Location = new Point(12, 13);
		pictureBox2.Name = "pictureBox2";
		pictureBox2.Size = new Size(52, 47);
		pictureBox2.TabIndex = 12;
		pictureBox2.TabStop = false;
		pictureBox2.Visible = false;
		AcceptButton = btnDownload;
		AutoScaleDimensions = new SizeF(6f, 13f);
		CancelButton = btnCancel;
		ClientSize = new Size(514, 143);
		Controls.Add(pictureBox2);
		Controls.Add(rbSCP);
		Controls.Add(rbTar);
		Controls.Add(pictureBox1);
		Controls.Add(btnRemoteBrowse);
		Controls.Add(progressBar1);
		Controls.Add(lblStats);
		Controls.Add(label1);
		Controls.Add(bntBrowse);
		Controls.Add(btnCancel);
		Controls.Add(btnDownload);
		Controls.Add(txtLocalDir);
		Controls.Add(txtRemoteDir);
		Controls.Add(lblLocalDir);
		Controls.Add(lblRemoteFile);
		MaximizeBox = false;
		MaximumSize = new Size(2000, 181);
		MinimizeBox = false;
		MinimumSize = new Size(380, 181);
		Name = "DirectoryTransferForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Download a Directory with SCP";
		((ISupportInitialize)pictureBox1).EndInit();
		((ISupportInitialize)pictureBox2).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
