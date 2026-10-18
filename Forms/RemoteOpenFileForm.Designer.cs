using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SmarTTY.Controls;

namespace SmarTTY.Forms;

public partial class RemoteOpenFileForm 
{
	private IContainer components = null;

	private ToolStrip tsControls;

	private ToolStripComboBox tscbDir;

	private ToolStripButton tspUp;

	private Button btnOpen;

	private Button btnCancel;

	private ColumnHeader _columnHeader;

	private ImageList _imageList;

	private TextBox tbFileName;

	private Label lFileName;

	private IndefiniteWaitControl iwcProgress;

	private Timer _timer;

	private DoubleBufferedListView lvFiles;

	private Label lblErrorInfo;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(RemoteOpenFileForm));
		_imageList = new ImageList(components);
		tsControls = new ToolStrip();
		tscbDir = new ToolStripComboBox();
		tspUp = new ToolStripButton();
		btnOpen = new Button();
		btnCancel = new Button();
		tbFileName = new TextBox();
		lFileName = new Label();
		_timer = new Timer(components);
		iwcProgress = new IndefiniteWaitControl();
		lvFiles = new DoubleBufferedListView();
		_columnHeader = new ColumnHeader();
		lblErrorInfo = new Label();
		tsControls.SuspendLayout();
		SuspendLayout();
		_imageList.ImageStream = (ImageListStreamer)resources.GetObject("ilFileSmallIcons.ImageStream");
		_imageList.TransparentColor = Color.Transparent;
		_imageList.Images.SetKeyName(0, "FolderOpen_16x16_72.png");
		_imageList.Images.SetKeyName(1, "DocumentHS.png");
		_imageList.Images.SetKeyName(2, "Generic_Application.ico");
		_imageList.Images.SetKeyName(3, "gear16.png");
		tsControls.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		tsControls.AutoSize = false;
		tsControls.Dock = DockStyle.None;
		tsControls.GripStyle = ToolStripGripStyle.Hidden;
		tsControls.Items.AddRange(new ToolStripItem[2] { tscbDir, tspUp });
		tsControls.Location = new Point(12, 9);
		tsControls.Name = "tsControls";
		tsControls.Size = new Size(460, 25);
		tsControls.TabIndex = 1;
		tsControls.TabStop = true;
		tsControls.Text = "toolStrip1";
		tsControls.Layout += tsControls_Layout;
		tscbDir.AutoSize = false;
		tscbDir.DropDownWidth = 121;
		tscbDir.Name = "tscbDir";
		tscbDir.Size = new Size(121, 23);
		tscbDir.SelectedIndexChanged += tscbDir_SelectedIndexChanged;
		tscbDir.KeyDown += tscbDir_KeyDown;
		tspUp.DisplayStyle = ToolStripItemDisplayStyle.Image;
		tspUp.Image = (Image)resources.GetObject("tspUp.Image");
		tspUp.ImageTransparentColor = Color.Magenta;
		tspUp.Name = "tspUp";
		tspUp.Size = new Size(23, 22);
		tspUp.Text = "Up";
		tspUp.Click += tspUp_Click;
		btnOpen.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnOpen.DialogResult = DialogResult.OK;
		btnOpen.Location = new Point(316, 227);
		btnOpen.Name = "btnOpen";
		btnOpen.Size = new Size(75, 23);
		btnOpen.TabIndex = 4;
		btnOpen.Text = "&Open";
		btnOpen.UseVisualStyleBackColor = true;
		btnOpen.Click += btnOpen_Click;
		btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		btnCancel.DialogResult = DialogResult.Cancel;
		btnCancel.Location = new Point(397, 227);
		btnCancel.Name = "btnCancel";
		btnCancel.Size = new Size(75, 23);
		btnCancel.TabIndex = 5;
		btnCancel.Text = "Cancel";
		btnCancel.UseVisualStyleBackColor = true;
		tbFileName.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		tbFileName.Location = new Point(70, 201);
		tbFileName.Name = "tbFileName";
		tbFileName.Size = new Size(402, 20);
		tbFileName.TabIndex = 5;
		tbFileName.KeyDown += tbFileName_KeyDown;
		lFileName.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
		lFileName.AutoSize = true;
		lFileName.Location = new Point(9, 204);
		lFileName.Name = "lFileName";
		lFileName.Size = new Size(55, 13);
		lFileName.TabIndex = 6;
		lFileName.Text = "File name:";
		_timer.Tick += OnPollTimer;
		iwcProgress.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		iwcProgress.CancelVisible = true;
		iwcProgress.DescriptionText = "Waiting to complete...";
		iwcProgress.Location = new Point(12, 174);
		iwcProgress.Margin = new Padding(3, 6, 3, 3);
		iwcProgress.MinimumSize = new Size(300, 47);
		iwcProgress.Name = "iwcProgress";
		iwcProgress.Size = new Size(460, 47);
		iwcProgress.TabIndex = 3;
		iwcProgress.Visible = false;
		iwcProgress.CancelRequested += OnCancelBusy;
		lvFiles.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		lvFiles.Columns.AddRange(new ColumnHeader[1] { _columnHeader });
		lvFiles.GridLines = true;
		lvFiles.HideSelection = false;
		lvFiles.Location = new Point(12, 37);
		lvFiles.MultiSelect = false;
		lvFiles.Name = "lvFiles";
		lvFiles.Size = new Size(460, 158);
		lvFiles.SmallImageList = _imageList;
		lvFiles.TabIndex = 2;
		lvFiles.UseCompatibleStateImageBehavior = false;
		lvFiles.View = View.List;
		lvFiles.SelectedIndexChanged += lvFiles_SelectedIndexChanged;
		lvFiles.DoubleClick += lvFiles_DoubleClick;
		lvFiles.KeyPress += lvFiles_KeyPress;
		_columnHeader.Text = "Name";
		_columnHeader.Width = 100;
		lblErrorInfo.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		lblErrorInfo.ForeColor = Color.Red;
		lblErrorInfo.Location = new Point(12, 227);
		lblErrorInfo.Name = "lblErrorInfo";
		lblErrorInfo.Size = new Size(298, 28);
		lblErrorInfo.TabIndex = 7;
		lblErrorInfo.Text = "Error information";
		lblErrorInfo.Visible = false;
		AcceptButton = btnOpen;
		AutoScaleDimensions = new SizeF(6f, 13f);
		CancelButton = btnCancel;
		ClientSize = new Size(484, 262);
		Controls.Add(lblErrorInfo);
		Controls.Add(iwcProgress);
		Controls.Add(lFileName);
		Controls.Add(tbFileName);
		Controls.Add(btnCancel);
		Controls.Add(btnOpen);
		Controls.Add(tsControls);
		Controls.Add(lvFiles);
		MaximizeBox = false;
		MinimizeBox = false;
		MinimumSize = new Size(340, 218);
		Name = "RemoteOpenFileForm";
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		Text = "Download a File with SCP";
		Load += RemoteOpenFileForm_Load;
		Resize += RemoteOpenFileForm_Resize;
		tsControls.ResumeLayout(performLayout: false);
		tsControls.PerformLayout();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
