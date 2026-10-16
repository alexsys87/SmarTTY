using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

public partial class DeviceTerminalControl 
{
	private IContainer components = null;

	private TerminalEmulatorControl terminalEmulatorControl1;

	private Panel pnlConnectionInProgress;

	private Label lblConnecting;

	private ProgressBar progressBar1;

	private ToolStrip toolStrip1;

	private ToolStripButton btnDisconnect;

	private ToolStripLabel lblConnection;

	private ToolStripButton btnReconnect;

	private LinkLabel linkLabel1;

	private ToolStripSplitButton btnSend;

	private ToolStripTextBox txtHexInput;

	private ToolStripLabel lblSend;

	private ToolStripMenuItem sendAsHEXToolStripMenuItem;

	private ToolStripMenuItem sendImmediatelyToolStripMenuItem;

	private HexTerminalControl hexTerminalControl1;

	private ToolStripButton toolStripButton1;

	private ToolStripButton toolStripButton3;

	private ToolStripButton toolStripButton2;

	private OpenFileDialog _openFileDialog;

	private SaveFileDialog _saveFileDialog;

	private ContextMenuStrip contextMenuStrip1;

	private ToolStripMenuItem copyToolStripMenuItem;

	private ToolStripMenuItem copyAllToolStripMenuItem;

	private ToolStripMenuItem pasteToolStripMenuItem;

	private ToolStripDropDownButton toolStripDropDownButton1;

	private ToolStripMenuItem terminalTypeToolStripMenuItem;

	private ToolStripMenuItem cbASCII;

	private ToolStripMenuItem cbHEX;

	private ToolStripMenuItem echoTypedCharactersToolStripMenuItem;

	private ToolStripMenuItem changeSettingsToolStripMenuItem;

	private ToolStripLabel lblBaudRate;

	private ToolStripComboBox cbBaudRate;

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
		ComponentResourceManager resources = new ComponentResourceManager(typeof(DeviceTerminalControl));
		HighlightStyle highlightStyle = new HighlightStyle();
		pnlConnectionInProgress = new Panel();
		linkLabel1 = new LinkLabel();
		progressBar1 = new ProgressBar();
		lblConnecting = new Label();
		toolStrip1 = new ToolStrip();
		lblConnection = new ToolStripLabel();
		btnDisconnect = new ToolStripButton();
		btnReconnect = new ToolStripButton();
		btnSend = new ToolStripSplitButton();
		sendAsHEXToolStripMenuItem = new ToolStripMenuItem();
		sendImmediatelyToolStripMenuItem = new ToolStripMenuItem();
		txtHexInput = new ToolStripTextBox();
		lblSend = new ToolStripLabel();
		toolStripButton1 = new ToolStripButton();
		toolStripButton3 = new ToolStripButton();
		toolStripButton2 = new ToolStripButton();
		toolStripDropDownButton1 = new ToolStripDropDownButton();
		terminalTypeToolStripMenuItem = new ToolStripMenuItem();
		cbASCII = new ToolStripMenuItem();
		cbHEX = new ToolStripMenuItem();
		echoTypedCharactersToolStripMenuItem = new ToolStripMenuItem();
		changeSettingsToolStripMenuItem = new ToolStripMenuItem();
		lblBaudRate = new ToolStripLabel();
		cbBaudRate = new ToolStripComboBox();
		contextMenuStrip1 = new ContextMenuStrip(components);
		copyToolStripMenuItem = new ToolStripMenuItem();
		copyAllToolStripMenuItem = new ToolStripMenuItem();
		pasteToolStripMenuItem = new ToolStripMenuItem();
		_openFileDialog = new OpenFileDialog();
		_saveFileDialog = new SaveFileDialog();
		hexTerminalControl1 = new HexTerminalControl();
		terminalEmulatorControl1 = new TerminalEmulatorControl();
		pnlConnectionInProgress.SuspendLayout();
		toolStrip1.SuspendLayout();
		contextMenuStrip1.SuspendLayout();
		SuspendLayout();
		pnlConnectionInProgress.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		pnlConnectionInProgress.Controls.Add(linkLabel1);
		pnlConnectionInProgress.Controls.Add(progressBar1);
		pnlConnectionInProgress.Controls.Add(lblConnecting);
		pnlConnectionInProgress.Location = new Point(0, 0);
		pnlConnectionInProgress.Name = "pnlConnectionInProgress";
		pnlConnectionInProgress.Size = new Size(634, 236);
		pnlConnectionInProgress.TabIndex = 1;
		pnlConnectionInProgress.Visible = false;
		linkLabel1.Anchor = AnchorStyles.None;
		linkLabel1.AutoSize = true;
		linkLabel1.Font = new Font("Microsoft Sans Serif", 14.25f, FontStyle.Regular, GraphicsUnit.Point, 0);
		linkLabel1.Location = new Point(266, 123);
		linkLabel1.Name = "linkLabel1";
		linkLabel1.Size = new Size(88, 24);
		linkLabel1.TabIndex = 2;
		((Label)linkLabel1).TabStop = true;
		linkLabel1.Text = "Try again";
		linkLabel1.LinkClicked += linkLabel1_LinkClicked;
		progressBar1.Anchor = AnchorStyles.Left | AnchorStyles.Right;
		progressBar1.Location = new Point(65, 123);
		progressBar1.Name = "progressBar1";
		progressBar1.Size = new Size(505, 23);
		progressBar1.Style = ProgressBarStyle.Marquee;
		progressBar1.TabIndex = 1;
		lblConnecting.Anchor = AnchorStyles.Left | AnchorStyles.Right;
		lblConnecting.Font = new Font("Microsoft Sans Serif", 14.25f, FontStyle.Regular, GraphicsUnit.Point, 0);
		lblConnecting.Location = new Point(3, 90);
		lblConnecting.Name = "lblConnecting";
		lblConnecting.Size = new Size(628, 30);
		lblConnecting.TabIndex = 0;
		lblConnecting.Text = "Connecting...";
		lblConnecting.TextAlign = ContentAlignment.TopCenter;
		toolStrip1.Items.AddRange(new ToolStripItem[12]
		{
			lblConnection, btnDisconnect, btnReconnect, btnSend, txtHexInput, lblSend, toolStripButton1, toolStripButton3, toolStripButton2, toolStripDropDownButton1,
			lblBaudRate, cbBaudRate
		});
		toolStrip1.Location = new Point(0, 0);
		toolStrip1.Name = "toolStrip1";
		toolStrip1.Size = new Size(634, 25);
		toolStrip1.TabIndex = 3;
		toolStrip1.Text = "Paste binary file";
		lblConnection.Name = "lblConnection";
		lblConnection.Size = new Size(65, 22);
		lblConnection.Text = "Connected";
		btnDisconnect.DisplayStyle = ToolStripItemDisplayStyle.Image;
		btnDisconnect.Image = (Image)resources.GetObject("btnDisconnect.Image");
		btnDisconnect.ImageTransparentColor = Color.Magenta;
		btnDisconnect.Name = "btnDisconnect";
		btnDisconnect.Size = new Size(23, 22);
		btnDisconnect.Text = "Disconnect";
		btnDisconnect.Click += btnDisconnect_Click;
		btnReconnect.DisplayStyle = ToolStripItemDisplayStyle.Image;
		btnReconnect.Image = (Image)resources.GetObject("btnReconnect.Image");
		btnReconnect.ImageTransparentColor = Color.Magenta;
		btnReconnect.Name = "btnReconnect";
		btnReconnect.Size = new Size(23, 22);
		btnReconnect.Text = "Reconnect";
		btnReconnect.Click += btnReconnect_Click;
		btnSend.Alignment = ToolStripItemAlignment.Right;
		btnSend.DisplayStyle = ToolStripItemDisplayStyle.Image;
		btnSend.DropDownItems.AddRange(new ToolStripItem[2] { sendAsHEXToolStripMenuItem, sendImmediatelyToolStripMenuItem });
		btnSend.Image = (Image)resources.GetObject("btnSend.Image");
		btnSend.ImageTransparentColor = Color.Magenta;
		btnSend.Name = "btnSend";
		btnSend.Size = new Size(32, 22);
		btnSend.Text = "Send";
		btnSend.ButtonClick += btnSend_ButtonClick;
		sendAsHEXToolStripMenuItem.Checked = true;
		sendAsHEXToolStripMenuItem.CheckState = CheckState.Checked;
		sendAsHEXToolStripMenuItem.Name = "sendAsHEXToolStripMenuItem";
		sendAsHEXToolStripMenuItem.Size = new Size(163, 22);
		sendAsHEXToolStripMenuItem.Text = "Send as HEX";
		sendAsHEXToolStripMenuItem.Click += sendAsHEXToolStripMenuItem_Click;
		sendImmediatelyToolStripMenuItem.Checked = true;
		sendImmediatelyToolStripMenuItem.CheckState = CheckState.Checked;
		sendImmediatelyToolStripMenuItem.Name = "sendImmediatelyToolStripMenuItem";
		sendImmediatelyToolStripMenuItem.Size = new Size(163, 22);
		sendImmediatelyToolStripMenuItem.Text = "Send as you type";
		sendImmediatelyToolStripMenuItem.Click += sendImmediatelyToolStripMenuItem_Click;
		txtHexInput.Alignment = ToolStripItemAlignment.Right;
		txtHexInput.Name = "txtHexInput";
		txtHexInput.Size = new Size(100, 25);
		txtHexInput.KeyDown += txtHexInput_KeyDown;
		txtHexInput.KeyPress += txtHexInput_KeyPress;
		txtHexInput.TextChanged += txtHexInput_TextChanged;
		lblSend.Alignment = ToolStripItemAlignment.Right;
		lblSend.Name = "lblSend";
		lblSend.Size = new Size(72, 22);
		lblSend.Text = "Send values:";
		toolStripButton1.DisplayStyle = ToolStripItemDisplayStyle.Image;
		toolStripButton1.Image = (Image)resources.GetObject("toolStripButton1.Image");
		toolStripButton1.ImageTransparentColor = Color.Magenta;
		toolStripButton1.Name = "toolStripButton1";
		toolStripButton1.Size = new Size(23, 22);
		toolStripButton1.Text = "Clear contents";
		toolStripButton1.Click += toolStripButton1_Click;
		toolStripButton3.DisplayStyle = ToolStripItemDisplayStyle.Image;
		toolStripButton3.Image = (Image)resources.GetObject("toolStripButton3.Image");
		toolStripButton3.ImageTransparentColor = Color.Magenta;
		toolStripButton3.Name = "toolStripButton3";
		toolStripButton3.Size = new Size(23, 22);
		toolStripButton3.Text = "Send a binary file";
		toolStripButton3.Click += toolStripButton3_Click;
		toolStripButton2.DisplayStyle = ToolStripItemDisplayStyle.Image;
		toolStripButton2.Image = (Image)resources.GetObject("toolStripButton2.Image");
		toolStripButton2.ImageTransparentColor = Color.Magenta;
		toolStripButton2.Name = "toolStripButton2";
		toolStripButton2.Size = new Size(23, 22);
		toolStripButton2.Text = "Save output to file";
		toolStripButton2.Click += toolStripButton2_Click;
		toolStripDropDownButton1.DisplayStyle = ToolStripItemDisplayStyle.Image;
		toolStripDropDownButton1.DropDownItems.AddRange(new ToolStripItem[3] { terminalTypeToolStripMenuItem, echoTypedCharactersToolStripMenuItem, changeSettingsToolStripMenuItem });
		toolStripDropDownButton1.Image = (Image)resources.GetObject("toolStripDropDownButton1.Image");
		toolStripDropDownButton1.ImageTransparentColor = Color.Magenta;
		toolStripDropDownButton1.Name = "toolStripDropDownButton1";
		toolStripDropDownButton1.Size = new Size(29, 22);
		toolStripDropDownButton1.Text = "Settings";
		terminalTypeToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[2] { cbASCII, cbHEX });
		terminalTypeToolStripMenuItem.Name = "terminalTypeToolStripMenuItem";
		terminalTypeToolStripMenuItem.Size = new Size(190, 22);
		terminalTypeToolStripMenuItem.Text = "Terminal type";
		cbASCII.Name = "cbASCII";
		cbASCII.Size = new Size(102, 22);
		cbASCII.Text = "ASCII";
		cbASCII.Click += cbASCII_Click;
		cbHEX.Name = "cbHEX";
		cbHEX.Size = new Size(102, 22);
		cbHEX.Text = "HEX";
		cbHEX.Click += cbHEX_Click;
		echoTypedCharactersToolStripMenuItem.Name = "echoTypedCharactersToolStripMenuItem";
		echoTypedCharactersToolStripMenuItem.Size = new Size(190, 22);
		echoTypedCharactersToolStripMenuItem.Text = "Echo typed characters";
		echoTypedCharactersToolStripMenuItem.Click += echoTypedCharactersToolStripMenuItem_Click;
		changeSettingsToolStripMenuItem.Name = "changeSettingsToolStripMenuItem";
		changeSettingsToolStripMenuItem.Size = new Size(190, 22);
		changeSettingsToolStripMenuItem.Text = "Connection settings...";
		changeSettingsToolStripMenuItem.Click += changeSettingsToolStripMenuItem_Click;
		lblBaudRate.Name = "lblBaudRate";
		lblBaudRate.Size = new Size(60, 22);
		lblBaudRate.Text = "Baud rate:";
		cbBaudRate.Items.AddRange(new object[9] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "74880", "115200" });
		cbBaudRate.Name = "cbBaudRate";
		cbBaudRate.Size = new Size(121, 25);
		cbBaudRate.SelectedIndexChanged += cbBaudRate_SelectedIndexChanged;
		cbBaudRate.Leave += cbBaudRate_Leave;
		cbBaudRate.KeyDown += cbBaudRate_KeyDown;
		contextMenuStrip1.Items.AddRange(new ToolStripItem[3] { copyToolStripMenuItem, copyAllToolStripMenuItem, pasteToolStripMenuItem });
		contextMenuStrip1.Name = "contextMenuStrip1";
		contextMenuStrip1.Size = new Size(200, 70);
		contextMenuStrip1.Opening += contextMenuStrip1_Opening;
		copyToolStripMenuItem.Image = SmarTTY.Properties.Resources.CopyHS;
		copyToolStripMenuItem.Name = "copyToolStripMenuItem";
		copyToolStripMenuItem.ShortcutKeyDisplayString = "Ctrl+Shift+C";
		copyToolStripMenuItem.Size = new Size(199, 22);
		copyToolStripMenuItem.Text = "Copy";
		copyToolStripMenuItem.Click += copyToolStripMenuItem_Click;
		copyAllToolStripMenuItem.Image = (Image)resources.GetObject("copyAllToolStripMenuItem.Image");
		copyAllToolStripMenuItem.Name = "copyAllToolStripMenuItem";
		copyAllToolStripMenuItem.Size = new Size(199, 22);
		copyAllToolStripMenuItem.Text = "Copy all";
		copyAllToolStripMenuItem.Click += copyAllToolStripMenuItem_Click;
		pasteToolStripMenuItem.Image = (Image)resources.GetObject("pasteToolStripMenuItem.Image");
		pasteToolStripMenuItem.Name = "pasteToolStripMenuItem";
		pasteToolStripMenuItem.ShortcutKeyDisplayString = "Ctrl+Shift+V";
		pasteToolStripMenuItem.Size = new Size(199, 22);
		pasteToolStripMenuItem.Text = "Paste";
		pasteToolStripMenuItem.Click += pasteToolStripMenuItem_Click;
		_openFileDialog.Filter = "All files|*.*";
		_openFileDialog.Title = "Send a binary file";
		_saveFileDialog.Filter = "All files|*.*";
		_saveFileDialog.Title = "Save output to a file";
		hexTerminalControl1.ContextMenuStrip = contextMenuStrip1;
		hexTerminalControl1.Dock = DockStyle.Fill;
		hexTerminalControl1.HideCaret = false;
		hexTerminalControl1.Location = new Point(0, 0);
		hexTerminalControl1.Name = "hexTerminalControl1";
		hexTerminalControl1.Size = new Size(634, 236);
		hexTerminalControl1.TabIndex = 4;
		hexTerminalControl1.KeyPress += hexTerminalControl1_KeyPress;
		hexTerminalControl1.KeyDown += OnTerminalKeyDown;
		terminalEmulatorControl1.ActiveBufferIndex = 0;
		terminalEmulatorControl1.AutoScroll = true;
		terminalEmulatorControl1.AutoWrap = true;
		terminalEmulatorControl1.BackColor = Color.Black;
		terminalEmulatorControl1.BellMode = TerminalEmulatorControl.BeepMode.None;
		terminalEmulatorControl1.BellSoundFile = null;
		terminalEmulatorControl1.NoLeftPadding = false;
		terminalEmulatorControl1.NormalTextColor = Color.White;
		terminalEmulatorControl1.CaptureDialogKeys = false;
		((ReadonlyTextBrowser)terminalEmulatorControl1).CaretBounds = new Rectangle(0, 0, 0, 0);
		((ReadonlyTextBrowser)terminalEmulatorControl1).TerminalMode = true;
		terminalEmulatorControl1.ContextMenuStrip = contextMenuStrip1;
		terminalEmulatorControl1.TabWidth = 8;
		terminalEmulatorControl1.Dock = DockStyle.Fill;
		terminalEmulatorControl1.LayoutSuspended = false;
		terminalEmulatorControl1.Font = new Font("Courier New", 12f, FontStyle.Regular, GraphicsUnit.Point, 204);
		terminalEmulatorControl1.ForeColor = Color.LightGray;
		terminalEmulatorControl1.NavigationLocked = true;
		terminalEmulatorControl1.NoHorizontalScroll = false;
		terminalEmulatorControl1.HideCaret = false;
		terminalEmulatorControl1.HideMargins = true;
		terminalEmulatorControl1.HorizontalScrollBarVisible = false;
		terminalEmulatorControl1.ForwardAltF4 = false;
		terminalEmulatorControl1.MaxChunkLength = 0;
		terminalEmulatorControl1.ShowLineNumbers = true;
		terminalEmulatorControl1.TopLine = 0;
		terminalEmulatorControl1.Location = new Point(0, 0);
		terminalEmulatorControl1.SelectionStyle = highlightStyle;
		terminalEmulatorControl1.Margin = new Padding(5, 4, 5, 4);
		terminalEmulatorControl1.UnusedValue = 0;
		terminalEmulatorControl1.MouseMode = TerminalEmulatorControl.MouseReportingMode.Off;
		terminalEmulatorControl1.Name = "terminalEmulatorControl1";
		terminalEmulatorControl1.EnterSequence = null;
		((ReadonlyTextBrowser)terminalEmulatorControl1).OverwriteMode = false;
		((EditableTextBrowser)terminalEmulatorControl1).ReadOnly = true;
		terminalEmulatorControl1.CaretWhenUnfocused = false;
		terminalEmulatorControl1.ShowUnrecognizedAsHex = false;
		terminalEmulatorControl1.SingleLine = false;
		terminalEmulatorControl1.Size = new Size(634, 236);
		((ReadonlySmartScrollBrowser)terminalEmulatorControl1).SmartAutoScroll = false;
		terminalEmulatorControl1.TabIndex = 0;
		((ReadonlyTextBrowser)terminalEmulatorControl1).TabSize = 4;
		terminalEmulatorControl1.ScrollbackLimit = int.MaxValue;
		terminalEmulatorControl1.Title = null;
		((ReadonlyTextBrowser)terminalEmulatorControl1).GroupBinaryDigits = false;
		terminalEmulatorControl1.IsModified = false;
		terminalEmulatorControl1.VerticalScrollBarVisible = true;
		terminalEmulatorControl1.Dispatcher = null;
		terminalEmulatorControl1.DataEntered += OnTerminalDataEntered;
		terminalEmulatorControl1.KeyDown += OnTerminalKeyDown;
		Controls.Add(toolStrip1);
		Controls.Add(hexTerminalControl1);
		Controls.Add(terminalEmulatorControl1);
		Controls.Add(pnlConnectionInProgress);
		Name = "DeviceTerminalControl";
		Size = new Size(634, 236);
		pnlConnectionInProgress.ResumeLayout(performLayout: false);
		pnlConnectionInProgress.PerformLayout();
		toolStrip1.ResumeLayout(performLayout: false);
		toolStrip1.PerformLayout();
		contextMenuStrip1.ResumeLayout(performLayout: false);
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
