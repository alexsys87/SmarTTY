using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SmarTTY.Controls;

namespace SmarTTY.Forms;

public partial class RemoteFolderBrowserForm 
{
	private IContainer components = null;

	private TreeView tvDirectories;

	private ImageList _imageList;

	private Button bCancel;

	private Button bOK;

	private IndefiniteWaitControl iwcProgress;

	private Timer _timer;

	private Label lblErrorInfo;

	private TextBox txtFolder;

	private Label lblFolder;

	private CheckBox cbRelative;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(RemoteFolderBrowserForm));
		_imageList = new ImageList(components);
		bOK = new Button();
		bCancel = new Button();
		tvDirectories = new TreeView();
		_timer = new Timer(components);
		lblErrorInfo = new Label();
		txtFolder = new TextBox();
		lblFolder = new Label();
		iwcProgress = new IndefiniteWaitControl();
		cbRelative = new CheckBox();
		SuspendLayout();
		_imageList.ImageStream = (ImageListStreamer)resources.GetObject("ilFolderIcons.ImageStream");
		_imageList.TransparentColor = Color.Transparent;
		_imageList.Images.SetKeyName(0, "FolderOpen_16x16_72.png");
		bOK.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		bOK.DialogResult = DialogResult.OK;
		bOK.Location = new Point(184, 273);
		bOK.Name = "bOK";
		bOK.Size = new Size(75, 23);
		bOK.TabIndex = 100;
		bOK.Text = "OK";
		bOK.UseVisualStyleBackColor = true;
		bCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		bCancel.DialogResult = DialogResult.Cancel;
		bCancel.Location = new Point(265, 273);
		bCancel.Name = "bCancel";
		bCancel.Size = new Size(75, 23);
		bCancel.TabIndex = 101;
		bCancel.Text = "Cancel";
		bCancel.UseVisualStyleBackColor = true;
		tvDirectories.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		tvDirectories.HideSelection = false;
		tvDirectories.ImageIndex = 0;
		tvDirectories.ImageList = _imageList;
		tvDirectories.Location = new Point(13, 13);
		tvDirectories.Name = "tvDirectories";
		tvDirectories.PathSeparator = "/";
		tvDirectories.SelectedImageIndex = 0;
		tvDirectories.ShowRootLines = false;
		tvDirectories.Size = new Size(327, 203);
		tvDirectories.TabIndex = 0;
		tvDirectories.BeforeExpand += tvDirectories_BeforeExpand;
		tvDirectories.AfterSelect += tvDirectories_AfterSelect;
		_timer.Tick += OnPollTimer;
		lblErrorInfo.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		lblErrorInfo.ForeColor = Color.Red;
		lblErrorInfo.Location = new Point(12, 270);
		lblErrorInfo.Name = "lblErrorInfo";
		lblErrorInfo.Size = new Size(166, 28);
		lblErrorInfo.TabIndex = 8;
		lblErrorInfo.Text = "Error information";
		lblErrorInfo.Visible = false;
		txtFolder.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		txtFolder.Location = new Point(57, 222);
		txtFolder.Name = "txtFolder";
		txtFolder.Size = new Size(283, 20);
		txtFolder.TabIndex = 3;
		txtFolder.KeyDown += txtFolder_KeyDown;
		lblFolder.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		lblFolder.AutoSize = true;
		lblFolder.Location = new Point(12, 225);
		lblFolder.Name = "lblFolder";
		lblFolder.Size = new Size(39, 13);
		lblFolder.TabIndex = 10;
		lblFolder.Text = "Folder:";
		iwcProgress.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		iwcProgress.CancelVisible = true;
		iwcProgress.DescriptionText = "Waiting to complete...";
		iwcProgress.Location = new Point(13, 169);
		iwcProgress.Margin = new Padding(3, 6, 3, 3);
		iwcProgress.MinimumSize = new Size(300, 47);
		iwcProgress.Name = "iwcProgress";
		iwcProgress.Size = new Size(327, 47);
		iwcProgress.TabIndex = 2;
		iwcProgress.Visible = false;
		iwcProgress.CancelRequested += OnCancelBusy;
		cbRelative.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		cbRelative.AutoSize = true;
		cbRelative.Location = new Point(13, 248);
		cbRelative.Name = "cbRelative";
		cbRelative.Size = new Size(185, 17);
		cbRelative.TabIndex = 102;
		cbRelative.Text = "Save relatively to project directory";
		cbRelative.UseVisualStyleBackColor = true;
		CancelButton = bCancel;
		ClientSize = new Size(352, 308);
		Controls.Add(cbRelative);
		Controls.Add(lblFolder);
		Controls.Add(txtFolder);
		Controls.Add(lblErrorInfo);
		Controls.Add(iwcProgress);
		Controls.Add(bOK);
		Controls.Add(bCancel);
		Controls.Add(tvDirectories);
		MinimumSize = new Size(368, 160);
		Name = "RemoteFolderBrowserForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Browse For Folder";
		Load += RemoteFolderBrowserForm_Load;
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
