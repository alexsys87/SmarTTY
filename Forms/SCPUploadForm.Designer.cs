using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class SCPUploadForm 
{	private IContainer components = null;

	private Label lblLocalFile;

	private TextBox txtLocalFile;

	private Label lblRemoteDir;

	private TextBox txtRemoteDir;

	private Button btnUpload;

	private Button btnCancel;

	private Button btnOpen;

	private OpenFileDialog _openFileDialog;

	private PictureBox pictureBox1;

	private Button bntBrowse;

	private RemoteFolderDialog _remoteFolderDialog;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(SCPUploadForm));
		lblLocalFile = new Label();
		txtLocalFile = new TextBox();
		lblRemoteDir = new Label();
		txtRemoteDir = new TextBox();
		btnUpload = new Button();
		btnCancel = new Button();
		btnOpen = new Button();
		_openFileDialog = new OpenFileDialog();
		pictureBox1 = new PictureBox();
		bntBrowse = new Button();
		_remoteFolderDialog = new RemoteFolderDialog(components);
		((ISupportInitialize)pictureBox1).BeginInit();
		SuspendLayout();
		lblLocalFile.AutoSize = true;
		lblLocalFile.Location = new Point(70, 17);
		lblLocalFile.Name = "lblLocalFile";
		lblLocalFile.Size = new Size(81, 13);
		lblLocalFile.TabIndex = 0;
		lblLocalFile.Text = "Local file name:";
		txtLocalFile.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtLocalFile.Location = new Point(168, 14);
		txtLocalFile.Name = "txtLocalFile";
		txtLocalFile.Size = new Size(295, 20);
		txtLocalFile.TabIndex = 1;
		lblRemoteDir.AutoSize = true;
		lblRemoteDir.Location = new Point(70, 43);
		lblRemoteDir.Name = "lblRemoteDir";
		lblRemoteDir.Size = new Size(90, 13);
		lblRemoteDir.TabIndex = 0;
		lblRemoteDir.Text = "Remote directory:";
		txtRemoteDir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		txtRemoteDir.Location = new Point(168, 40);
		txtRemoteDir.Name = "txtRemoteDir";
		txtRemoteDir.Size = new Size(295, 20);
		txtRemoteDir.TabIndex = 3;
		btnUpload.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnUpload.DialogResult = DialogResult.OK;
		btnUpload.Location = new Point(346, 67);
		btnUpload.Name = "btnUpload";
		btnUpload.Size = new Size(75, 23);
		btnUpload.TabIndex = 4;
		btnUpload.Text = "&Upload";
		btnUpload.UseVisualStyleBackColor = true;
		btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnCancel.DialogResult = DialogResult.Cancel;
		btnCancel.Location = new Point(427, 67);
		btnCancel.Name = "btnCancel";
		btnCancel.Size = new Size(75, 23);
		btnCancel.TabIndex = 5;
		btnCancel.Text = "Cancel";
		btnCancel.UseVisualStyleBackColor = true;
		btnOpen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		btnOpen.Image = (Image)resources.GetObject("btnOpen.Image");
		btnOpen.Location = new Point(469, 12);
		btnOpen.Name = "btnOpen";
		btnOpen.Size = new Size(33, 23);
		btnOpen.TabIndex = 2;
		btnOpen.UseVisualStyleBackColor = true;
		btnOpen.Click += btnOpen_Click;
		_openFileDialog.Filter = "All files|*.*";
		pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
		pictureBox1.Location = new Point(12, 13);
		pictureBox1.Name = "pictureBox1";
		pictureBox1.Size = new Size(52, 47);
		pictureBox1.TabIndex = 11;
		pictureBox1.TabStop = false;
		bntBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		bntBrowse.Image = (Image)resources.GetObject("bntBrowse.Image");
		bntBrowse.Location = new Point(469, 38);
		bntBrowse.Name = "bntBrowse";
		bntBrowse.Size = new Size(33, 23);
		bntBrowse.TabIndex = 12;
		bntBrowse.UseVisualStyleBackColor = true;
		bntBrowse.Click += bntBrowse_Click;
		_remoteFolderDialog.InitialFolder = null;
		_remoteFolderDialog.HostName = null;
		AcceptButton = btnUpload;
		AutoScaleDimensions = new SizeF(6f, 13f);
		CancelButton = btnCancel;
		ClientSize = new Size(514, 102);
		Controls.Add(bntBrowse);
		Controls.Add(pictureBox1);
		Controls.Add(btnOpen);
		Controls.Add(btnCancel);
		Controls.Add(btnUpload);
		Controls.Add(txtRemoteDir);
		Controls.Add(txtLocalFile);
		Controls.Add(lblRemoteDir);
		Controls.Add(lblLocalFile);
		MaximizeBox = false;
		MaximumSize = new Size(2000, 140);
		MinimizeBox = false;
		MinimumSize = new Size(300, 140);
		Name = "SCPUploadForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Upload a File with SCP";
		((ISupportInitialize)pictureBox1).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
