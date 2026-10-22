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

	private ToolStripSplitButton toolStripButton3;

	private ToolStripMenuItem sendBinaryFileToolStripMenuItem;

	private ToolStripMenuItem sendFileViaXMODEMToolStripMenuItem;

	private ToolStripMenuItem sendFileViaXMODEM1KToolStripMenuItem;

	private ToolStripMenuItem receiveFileViaXMODEMToolStripMenuItem;

	private ToolStripMenuItem sendFilesViaZModemToolStripMenuItem;

	private ToolStripMenuItem receiveFilesViaZModemToolStripMenuItem;

	private ToolStripMenuItem autoReceiveZModemToolStripMenuItem;

	private ToolStripMenuItem escapeZModemControlToolStripMenuItem;

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

	private ToolStripMenuItem cbBoth;

	private ToolStripMenuItem echoTypedCharactersToolStripMenuItem;

	private ToolStripMenuItem changeSettingsToolStripMenuItem;

	private ToolStripLabel lblBaudRate;

	private ToolStripComboBox cbBaudRate;

	private ToolStripLabel lblEncoding;

	private ToolStripComboBox cbEncoding;

	private ToolStripButton btnLog;

	private ToolStripButton btnReplay;

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
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(DeviceTerminalControl));
            SmarTTY.TextEditing.HighlightStyle highlightStyle1 = new SmarTTY.TextEditing.HighlightStyle();
            this.pnlConnectionInProgress = new System.Windows.Forms.Panel();
            this.linkLabel1 = new System.Windows.Forms.LinkLabel();
            this.progressBar1 = new System.Windows.Forms.ProgressBar();
            this.lblConnecting = new System.Windows.Forms.Label();
            this.toolStrip1 = new System.Windows.Forms.ToolStrip();
            this.lblConnection = new System.Windows.Forms.ToolStripLabel();
            this.btnDisconnect = new System.Windows.Forms.ToolStripButton();
            this.btnReconnect = new System.Windows.Forms.ToolStripButton();
            this.btnSend = new System.Windows.Forms.ToolStripSplitButton();
            this.sendAsHEXToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.sendImmediatelyToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.txtHexInput = new System.Windows.Forms.ToolStripTextBox();
            this.lblSend = new System.Windows.Forms.ToolStripLabel();
            this.toolStripButton1 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton3 = new System.Windows.Forms.ToolStripSplitButton();
            this.sendBinaryFileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.sendFileViaXMODEMToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.sendFileViaXMODEM1KToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.receiveFileViaXMODEMToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.sendFilesViaZModemToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.receiveFilesViaZModemToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.autoReceiveZModemToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.escapeZModemControlToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripButton2 = new System.Windows.Forms.ToolStripButton();
            this.btnLog = new System.Windows.Forms.ToolStripButton();
            this.btnReplay = new System.Windows.Forms.ToolStripButton();
            this.toolStripDropDownButton1 = new System.Windows.Forms.ToolStripDropDownButton();
            this.terminalTypeToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.cbASCII = new System.Windows.Forms.ToolStripMenuItem();
            this.cbHEX = new System.Windows.Forms.ToolStripMenuItem();
            this.cbBoth = new System.Windows.Forms.ToolStripMenuItem();
            this.echoTypedCharactersToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.changeSettingsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.lblBaudRate = new System.Windows.Forms.ToolStripLabel();
            this.cbBaudRate = new System.Windows.Forms.ToolStripComboBox();
            this.lblEncoding = new System.Windows.Forms.ToolStripLabel();
            this.cbEncoding = new System.Windows.Forms.ToolStripComboBox();
            this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.copyToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.copyAllToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.pasteToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._openFileDialog = new System.Windows.Forms.OpenFileDialog();
            this._saveFileDialog = new System.Windows.Forms.SaveFileDialog();
            this.hexTerminalControl1 = new SmarTTY.Controls.HexTerminalControl();
            this.terminalEmulatorControl1 = new SmarTTY.Controls.TerminalEmulatorControl();
            this.pnlConnectionInProgress.SuspendLayout();
            this.toolStrip1.SuspendLayout();
            this.contextMenuStrip1.SuspendLayout();
            this.SuspendLayout();
            // 
            // pnlConnectionInProgress
            // 
            this.pnlConnectionInProgress.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pnlConnectionInProgress.Controls.Add(this.linkLabel1);
            this.pnlConnectionInProgress.Controls.Add(this.progressBar1);
            this.pnlConnectionInProgress.Controls.Add(this.lblConnecting);
            this.pnlConnectionInProgress.Location = new System.Drawing.Point(0, 0);
            this.pnlConnectionInProgress.Name = "pnlConnectionInProgress";
            this.pnlConnectionInProgress.Size = new System.Drawing.Size(634, 236);
            this.pnlConnectionInProgress.TabIndex = 1;
            this.pnlConnectionInProgress.Visible = false;
            // 
            // linkLabel1
            // 
            this.linkLabel1.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.linkLabel1.AutoSize = true;
            this.linkLabel1.Font = new System.Drawing.Font("Microsoft Sans Serif", 14.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.linkLabel1.Location = new System.Drawing.Point(266, 123);
            this.linkLabel1.Name = "linkLabel1";
            this.linkLabel1.Size = new System.Drawing.Size(88, 24);
            this.linkLabel1.TabIndex = 2;
            this.linkLabel1.TabStop = true;
            this.linkLabel1.Text = "Try again";
            // 
            // progressBar1
            // 
            this.progressBar1.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right)));
            this.progressBar1.Location = new System.Drawing.Point(65, 123);
            this.progressBar1.Name = "progressBar1";
            this.progressBar1.Size = new System.Drawing.Size(505, 23);
            this.progressBar1.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            this.progressBar1.TabIndex = 1;
            // 
            // lblConnecting
            // 
            this.lblConnecting.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right)));
            this.lblConnecting.Font = new System.Drawing.Font("Microsoft Sans Serif", 14.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblConnecting.Location = new System.Drawing.Point(3, 90);
            this.lblConnecting.Name = "lblConnecting";
            this.lblConnecting.Size = new System.Drawing.Size(628, 30);
            this.lblConnecting.TabIndex = 0;
            this.lblConnecting.Text = "Connecting...";
            this.lblConnecting.TextAlign = System.Drawing.ContentAlignment.TopCenter;
            // 
            // toolStrip1
            // 
            this.toolStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.lblConnection,
            this.btnDisconnect,
            this.btnReconnect,
            this.btnSend,
            this.txtHexInput,
            this.lblSend,
            this.toolStripButton1,
            this.toolStripButton3,
            this.toolStripButton2,
            this.btnLog,
            this.btnReplay,
            this.toolStripDropDownButton1,
            this.lblBaudRate,
            this.cbBaudRate,
            this.lblEncoding,
            this.cbEncoding});
            this.toolStrip1.Location = new System.Drawing.Point(0, 0);
            this.toolStrip1.Name = "toolStrip1";
            this.toolStrip1.Size = new System.Drawing.Size(634, 27);
            this.toolStrip1.TabIndex = 3;
            this.toolStrip1.Text = "Paste binary file";
            // 
            // lblConnection
            // 
            this.lblConnection.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            this.lblConnection.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.lblConnection.Name = "lblConnection";
            this.lblConnection.Size = new System.Drawing.Size(65, 24);
            this.lblConnection.Text = "Connected";
            // 
            // btnDisconnect
            // 
            this.btnDisconnect.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.btnDisconnect.Image = ((System.Drawing.Image)(resources.GetObject("btnDisconnect.Image")));
            this.btnDisconnect.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.btnDisconnect.Name = "btnDisconnect";
            this.btnDisconnect.Size = new System.Drawing.Size(23, 24);
            this.btnDisconnect.Text = "Disconnect";
            // 
            // btnReconnect
            // 
            this.btnReconnect.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.btnReconnect.Image = ((System.Drawing.Image)(resources.GetObject("btnReconnect.Image")));
            this.btnReconnect.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.btnReconnect.Name = "btnReconnect";
            this.btnReconnect.Size = new System.Drawing.Size(23, 24);
            this.btnReconnect.Text = "Reconnect";
            // 
            // btnSend
            // 
            this.btnSend.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right;
            this.btnSend.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.btnSend.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.sendAsHEXToolStripMenuItem,
            this.sendImmediatelyToolStripMenuItem});
            this.btnSend.Image = ((System.Drawing.Image)(resources.GetObject("btnSend.Image")));
            this.btnSend.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.btnSend.Name = "btnSend";
            this.btnSend.Size = new System.Drawing.Size(32, 24);
            this.btnSend.Text = "Send";
            // 
            // sendAsHEXToolStripMenuItem
            // 
            this.sendAsHEXToolStripMenuItem.Checked = true;
            this.sendAsHEXToolStripMenuItem.CheckState = System.Windows.Forms.CheckState.Checked;
            this.sendAsHEXToolStripMenuItem.Name = "sendAsHEXToolStripMenuItem";
            this.sendAsHEXToolStripMenuItem.Size = new System.Drawing.Size(163, 22);
            this.sendAsHEXToolStripMenuItem.Text = "Send as HEX";
            // 
            // sendImmediatelyToolStripMenuItem
            // 
            this.sendImmediatelyToolStripMenuItem.Checked = true;
            this.sendImmediatelyToolStripMenuItem.CheckState = System.Windows.Forms.CheckState.Checked;
            this.sendImmediatelyToolStripMenuItem.Name = "sendImmediatelyToolStripMenuItem";
            this.sendImmediatelyToolStripMenuItem.Size = new System.Drawing.Size(163, 22);
            this.sendImmediatelyToolStripMenuItem.Text = "Send as you type";
            // 
            // txtHexInput
            // 
            this.txtHexInput.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right;
            this.txtHexInput.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.txtHexInput.Name = "txtHexInput";
            this.txtHexInput.Size = new System.Drawing.Size(100, 27);
            // 
            // lblSend
            // 
            this.lblSend.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right;
            this.lblSend.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            this.lblSend.Name = "lblSend";
            this.lblSend.Size = new System.Drawing.Size(72, 24);
            this.lblSend.Text = "Send values:";
            // 
            // toolStripButton1
            // 
            this.toolStripButton1.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.toolStripButton1.Image = ((System.Drawing.Image)(resources.GetObject("toolStripButton1.Image")));
            this.toolStripButton1.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton1.Name = "toolStripButton1";
            this.toolStripButton1.Size = new System.Drawing.Size(23, 24);
            this.toolStripButton1.Text = "Clear contents";
            // 
            // toolStripButton3
            // 
            this.toolStripButton3.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.toolStripButton3.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.sendBinaryFileToolStripMenuItem,
            this.sendFileViaXMODEMToolStripMenuItem,
            this.sendFileViaXMODEM1KToolStripMenuItem,
            this.receiveFileViaXMODEMToolStripMenuItem,
            this.sendFilesViaZModemToolStripMenuItem,
            this.receiveFilesViaZModemToolStripMenuItem,
            this.autoReceiveZModemToolStripMenuItem,
            this.escapeZModemControlToolStripMenuItem});
            this.toolStripButton3.Image = ((System.Drawing.Image)(resources.GetObject("toolStripButton3.Image")));
            this.toolStripButton3.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton3.Name = "toolStripButton3";
            this.toolStripButton3.Size = new System.Drawing.Size(32, 24);
            this.toolStripButton3.Text = "File transfer";
            // 
            // sendBinaryFileToolStripMenuItem
            // 
            this.sendBinaryFileToolStripMenuItem.Name = "sendBinaryFileToolStripMenuItem";
            this.sendBinaryFileToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.sendBinaryFileToolStripMenuItem.Text = "Send binary file";
            // 
            // sendFileViaXMODEMToolStripMenuItem
            // 
            this.sendFileViaXMODEMToolStripMenuItem.Name = "sendFileViaXMODEMToolStripMenuItem";
            this.sendFileViaXMODEMToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.sendFileViaXMODEMToolStripMenuItem.Text = "Send file via XMODEM...";
            // 
            // sendFileViaXMODEM1KToolStripMenuItem
            // 
            this.sendFileViaXMODEM1KToolStripMenuItem.Name = "sendFileViaXMODEM1KToolStripMenuItem";
            this.sendFileViaXMODEM1KToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.sendFileViaXMODEM1KToolStripMenuItem.Text = "Send file via XMODEM-1K...";
            // 
            // receiveFileViaXMODEMToolStripMenuItem
            // 
            this.receiveFileViaXMODEMToolStripMenuItem.Name = "receiveFileViaXMODEMToolStripMenuItem";
            this.receiveFileViaXMODEMToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.receiveFileViaXMODEMToolStripMenuItem.Text = "Receive file via XMODEM...";
            // 
            // sendFilesViaZModemToolStripMenuItem
            // 
            this.sendFilesViaZModemToolStripMenuItem.Name = "sendFilesViaZModemToolStripMenuItem";
            this.sendFilesViaZModemToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.sendFilesViaZModemToolStripMenuItem.Text = "Send files via ZMODEM...";
            // 
            // receiveFilesViaZModemToolStripMenuItem
            // 
            this.receiveFilesViaZModemToolStripMenuItem.Name = "receiveFilesViaZModemToolStripMenuItem";
            this.receiveFilesViaZModemToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.receiveFilesViaZModemToolStripMenuItem.Text = "Receive files via ZMODEM...";
            // 
            // autoReceiveZModemToolStripMenuItem
            // 
            this.autoReceiveZModemToolStripMenuItem.Checked = true;
            this.autoReceiveZModemToolStripMenuItem.CheckOnClick = true;
            this.autoReceiveZModemToolStripMenuItem.CheckState = System.Windows.Forms.CheckState.Checked;
            this.autoReceiveZModemToolStripMenuItem.Name = "autoReceiveZModemToolStripMenuItem";
            this.autoReceiveZModemToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.autoReceiveZModemToolStripMenuItem.Text = "Start ZMODEM receive automatically";
            // 
            // escapeZModemControlToolStripMenuItem
            // 
            this.escapeZModemControlToolStripMenuItem.CheckOnClick = true;
            this.escapeZModemControlToolStripMenuItem.Name = "escapeZModemControlToolStripMenuItem";
            this.escapeZModemControlToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.escapeZModemControlToolStripMenuItem.Text = "ZMODEM: escape control characters";
            // 
            // toolStripButton2
            // 
            this.toolStripButton2.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.toolStripButton2.Image = ((System.Drawing.Image)(resources.GetObject("toolStripButton2.Image")));
            this.toolStripButton2.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton2.Name = "toolStripButton2";
            this.toolStripButton2.Size = new System.Drawing.Size(23, 24);
            this.toolStripButton2.Text = "Save output to file";
            // 
            // btnLog
            // 
            this.btnLog.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            this.btnLog.Name = "btnLog";
            this.btnLog.Size = new System.Drawing.Size(55, 24);
            this.btnLog.Text = "Start log";
            this.btnLog.ToolTipText = "Continuously record received data to a file";
            // 
            // btnReplay
            // 
            this.btnReplay.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            this.btnReplay.Name = "btnReplay";
            this.btnReplay.Size = new System.Drawing.Size(46, 24);
            this.btnReplay.Text = "Replay";
            this.btnReplay.ToolTipText = "Play a recorded log of received data back into this terminal";
            // 
            // toolStripDropDownButton1
            // 
            this.toolStripDropDownButton1.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.toolStripDropDownButton1.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.terminalTypeToolStripMenuItem,
            this.echoTypedCharactersToolStripMenuItem,
            this.changeSettingsToolStripMenuItem});
            this.toolStripDropDownButton1.Image = ((System.Drawing.Image)(resources.GetObject("toolStripDropDownButton1.Image")));
            this.toolStripDropDownButton1.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripDropDownButton1.Name = "toolStripDropDownButton1";
            this.toolStripDropDownButton1.Size = new System.Drawing.Size(29, 24);
            this.toolStripDropDownButton1.Text = "Settings";
            // 
            // terminalTypeToolStripMenuItem
            // 
            this.terminalTypeToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.cbASCII,
            this.cbHEX,
            this.cbBoth});
            this.terminalTypeToolStripMenuItem.Name = "terminalTypeToolStripMenuItem";
            this.terminalTypeToolStripMenuItem.Size = new System.Drawing.Size(190, 22);
            this.terminalTypeToolStripMenuItem.Text = "Terminal type";
            // 
            // cbASCII
            // 
            this.cbASCII.Name = "cbASCII";
            this.cbASCII.Size = new System.Drawing.Size(131, 22);
            this.cbASCII.Text = "ASCII";
            // 
            // cbHEX
            // 
            this.cbHEX.Name = "cbHEX";
            this.cbHEX.Size = new System.Drawing.Size(131, 22);
            this.cbHEX.Text = "HEX";
            // 
            // cbBoth
            // 
            this.cbBoth.Name = "cbBoth";
            this.cbBoth.Size = new System.Drawing.Size(131, 22);
            this.cbBoth.Text = "Text + HEX";
            // 
            // echoTypedCharactersToolStripMenuItem
            // 
            this.echoTypedCharactersToolStripMenuItem.Name = "echoTypedCharactersToolStripMenuItem";
            this.echoTypedCharactersToolStripMenuItem.Size = new System.Drawing.Size(190, 22);
            this.echoTypedCharactersToolStripMenuItem.Text = "Echo typed characters";
            // 
            // changeSettingsToolStripMenuItem
            // 
            this.changeSettingsToolStripMenuItem.Name = "changeSettingsToolStripMenuItem";
            this.changeSettingsToolStripMenuItem.Size = new System.Drawing.Size(190, 22);
            this.changeSettingsToolStripMenuItem.Text = "Connection settings...";
            // 
            // lblBaudRate
            // 
            this.lblBaudRate.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            this.lblBaudRate.Name = "lblBaudRate";
            this.lblBaudRate.Size = new System.Drawing.Size(60, 24);
            this.lblBaudRate.Text = "Baud rate:";
            // 
            // cbBaudRate
            // 
            this.cbBaudRate.Items.AddRange(new object[] {
            "1200",
            "2400",
            "4800",
            "9600",
            "19200",
            "38400",
            "57600",
            "74880",
            "115200",
            "230400",
            "460800",
            "921600",
            "1000000",
            "2000000"});
            this.cbBaudRate.Name = "cbBaudRate";
            this.cbBaudRate.Size = new System.Drawing.Size(121, 23);
            // 
            // lblEncoding
            // 
            this.lblEncoding.Name = "lblEncoding";
            this.lblEncoding.Size = new System.Drawing.Size(60, 15);
            this.lblEncoding.Text = "Encoding:";
            // 
            // cbEncoding
            // 
            this.cbEncoding.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbEncoding.Name = "cbEncoding";
            this.cbEncoding.Size = new System.Drawing.Size(170, 23);
            this.cbEncoding.ToolTipText = "Text encoding used to decode received data and to encode typed and pasted text";
            // 
            // contextMenuStrip1
            // 
            this.contextMenuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.copyToolStripMenuItem,
            this.copyAllToolStripMenuItem,
            this.pasteToolStripMenuItem});
            this.contextMenuStrip1.Name = "contextMenuStrip1";
            this.contextMenuStrip1.Size = new System.Drawing.Size(177, 70);
            // 
            // copyToolStripMenuItem
            // 
            this.copyToolStripMenuItem.Image = ((System.Drawing.Image)(resources.GetObject("copyToolStripMenuItem.Image")));
            this.copyToolStripMenuItem.Name = "copyToolStripMenuItem";
            this.copyToolStripMenuItem.ShortcutKeyDisplayString = "Ctrl+Shift+C";
            this.copyToolStripMenuItem.Size = new System.Drawing.Size(176, 22);
            this.copyToolStripMenuItem.Text = "Copy";
            // 
            // copyAllToolStripMenuItem
            // 
            this.copyAllToolStripMenuItem.Image = ((System.Drawing.Image)(resources.GetObject("copyAllToolStripMenuItem.Image")));
            this.copyAllToolStripMenuItem.Name = "copyAllToolStripMenuItem";
            this.copyAllToolStripMenuItem.Size = new System.Drawing.Size(176, 22);
            this.copyAllToolStripMenuItem.Text = "Copy all";
            // 
            // pasteToolStripMenuItem
            // 
            this.pasteToolStripMenuItem.Image = ((System.Drawing.Image)(resources.GetObject("pasteToolStripMenuItem.Image")));
            this.pasteToolStripMenuItem.Name = "pasteToolStripMenuItem";
            this.pasteToolStripMenuItem.ShortcutKeyDisplayString = "Ctrl+Shift+V";
            this.pasteToolStripMenuItem.Size = new System.Drawing.Size(176, 22);
            this.pasteToolStripMenuItem.Text = "Paste";
            // 
            // _openFileDialog
            // 
            this._openFileDialog.Filter = "All files|*.*";
            this._openFileDialog.Title = "Send a binary file";
            // 
            // _saveFileDialog
            // 
            this._saveFileDialog.Filter = "All files|*.*";
            this._saveFileDialog.Title = "Save output to a file";
            // 
            // hexTerminalControl1
            // 
            this.hexTerminalControl1.ContextMenuStrip = this.contextMenuStrip1;
            this.hexTerminalControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.hexTerminalControl1.HideCaret = false;
            this.hexTerminalControl1.Location = new System.Drawing.Point(0, 0);
            this.hexTerminalControl1.Name = "hexTerminalControl1";
            this.hexTerminalControl1.Size = new System.Drawing.Size(634, 236);
            this.hexTerminalControl1.TabIndex = 4;
            this.hexTerminalControl1.TextScrollBarVisible = true;
            // 
            // terminalEmulatorControl1
            // 
            this.terminalEmulatorControl1.ActiveBufferIndex = 0;
            this.terminalEmulatorControl1.AutoScroll = true;
            this.terminalEmulatorControl1.AutoWrap = true;
            this.terminalEmulatorControl1.BackColor = System.Drawing.Color.Black;
            this.terminalEmulatorControl1.BellMode = SmarTTY.Controls.TerminalEmulatorControl.BeepMode.None;
            this.terminalEmulatorControl1.BellSoundFile = null;
            this.terminalEmulatorControl1.CaptureDialogKeys = false;
            this.terminalEmulatorControl1.CaretBounds = new System.Drawing.Rectangle(0, 0, 0, 0);
            this.terminalEmulatorControl1.CaretWhenUnfocused = false;
            this.terminalEmulatorControl1.ContextMenuStrip = this.contextMenuStrip1;
            this.terminalEmulatorControl1.Dispatcher = null;
            this.terminalEmulatorControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.terminalEmulatorControl1.EnterSequence = null;
            this.terminalEmulatorControl1.Font = new System.Drawing.Font("Courier New", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(204)));
            this.terminalEmulatorControl1.ForeColor = System.Drawing.Color.LightGray;
            this.terminalEmulatorControl1.ForwardAltF4 = false;
            this.terminalEmulatorControl1.GroupBinaryDigits = false;
            this.terminalEmulatorControl1.HideCaret = false;
            this.terminalEmulatorControl1.HideMargins = true;
            this.terminalEmulatorControl1.HorizontalScrollBarVisible = false;
            this.terminalEmulatorControl1.IsModified = false;
            this.terminalEmulatorControl1.LayoutSuspended = false;
            this.terminalEmulatorControl1.Location = new System.Drawing.Point(0, 0);
            this.terminalEmulatorControl1.Margin = new System.Windows.Forms.Padding(5, 4, 5, 4);
            this.terminalEmulatorControl1.MaxChunkLength = 0;
            this.terminalEmulatorControl1.MouseMode = SmarTTY.Controls.TerminalEmulatorControl.MouseReportingMode.Off;
            this.terminalEmulatorControl1.Name = "terminalEmulatorControl1";
            this.terminalEmulatorControl1.NavigationLocked = true;
            this.terminalEmulatorControl1.NoHorizontalScroll = false;
            this.terminalEmulatorControl1.NoLeftPadding = false;
            this.terminalEmulatorControl1.NormalTextColor = System.Drawing.Color.White;
            this.terminalEmulatorControl1.OverwriteMode = false;
            this.terminalEmulatorControl1.ReadOnly = true;
            this.terminalEmulatorControl1.ScrollbackLimit = 2147483647;
            this.terminalEmulatorControl1.SelectionStyle = highlightStyle1;
            this.terminalEmulatorControl1.ShowLineNumbers = true;
            this.terminalEmulatorControl1.ShowUnrecognizedAsHex = false;
            this.terminalEmulatorControl1.SingleLine = false;
            this.terminalEmulatorControl1.Size = new System.Drawing.Size(634, 236);
            this.terminalEmulatorControl1.SmartAutoScroll = false;
            this.terminalEmulatorControl1.TabIndex = 0;
            this.terminalEmulatorControl1.TabSize = 4;
            this.terminalEmulatorControl1.TabWidth = 8;
            this.terminalEmulatorControl1.TerminalMode = true;
            this.terminalEmulatorControl1.Title = null;
            this.terminalEmulatorControl1.TopLine = 0;
            this.terminalEmulatorControl1.UnusedValue = 0;
            this.terminalEmulatorControl1.VerticalScrollBarVisible = true;
            // 
            // DeviceTerminalControl
            // 
            this.Controls.Add(this.toolStrip1);
            this.Controls.Add(this.hexTerminalControl1);
            this.Controls.Add(this.terminalEmulatorControl1);
            this.Controls.Add(this.pnlConnectionInProgress);
            this.Name = "DeviceTerminalControl";
            this.Size = new System.Drawing.Size(634, 236);
            this.pnlConnectionInProgress.ResumeLayout(false);
            this.pnlConnectionInProgress.PerformLayout();
            this.toolStrip1.ResumeLayout(false);
            this.toolStrip1.PerformLayout();
            this.contextMenuStrip1.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

	}

}
