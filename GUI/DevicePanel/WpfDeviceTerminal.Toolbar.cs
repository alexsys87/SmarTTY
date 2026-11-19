using SmarTTY.Terminal.Input;
using SmarTTY.Controls;
using SmarTTY.GUI.TerminalView;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using SmarTTY.TextEditing;

namespace SmarTTY.GUI.DevicePanel;

// The tool bar and the context menu of the device panel: the WinForms items of the old panel's designer, as they were (stage 4 makes them
// WPF); the panel hosts the tool bar in a WindowsFormsHost and shows the menu as a popup.
public partial class WpfDeviceTerminal
{
	private IContainer components = null;

	private ToolStrip toolStrip1;

	private ToolStripButton btnDisconnect;

	private ToolStripLabel lblConnection;

	private ToolStripButton btnReconnect;

	private ToolStripSplitButton btnSend;

	private ToolStripTextBox txtHexInput;

	private ToolStripLabel lblSend;

	private ToolStripMenuItem sendAsHEXToolStripMenuItem;

	private ToolStripMenuItem sendImmediatelyToolStripMenuItem;

	private ToolStripButton toolStripButton1;

	private ToolStripSplitButton toolStripButton3;

	private ToolStripMenuItem sendBinaryFileToolStripMenuItem;

	private ToolStripMenuItem sendFileViaXMODEMToolStripMenuItem;

	private ToolStripMenuItem sendFileViaXMODEM1KToolStripMenuItem;

	private ToolStripMenuItem receiveFileViaXMODEMToolStripMenuItem;

	private ToolStripMenuItem sendFilesViaYMODEMToolStripMenuItem;

	private ToolStripMenuItem receiveFilesViaYMODEMToolStripMenuItem;

	private ToolStripMenuItem receiveFilesViaYMODEMGToolStripMenuItem;

	private ToolStripMenuItem sendFilesViaZModemToolStripMenuItem;

	private ToolStripMenuItem receiveFilesViaZModemToolStripMenuItem;

	private ToolStripMenuItem autoReceiveZModemToolStripMenuItem;

	private ToolStripMenuItem escapeZModemControlToolStripMenuItem;
	private ToolStripMenuItem sendFilesViaKermitToolStripMenuItem;

	private ToolStripMenuItem receiveFilesViaKermitToolStripMenuItem;

	private ToolStripButton toolStripButton2;

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

	private void DisposeComponents(bool disposing)
	{
		if (disposing && components != null)
		{
			components.Dispose();
		}
	}

	private void InitializeComponent()
	{
            this.components = new System.ComponentModel.Container();
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
            this.sendFilesViaYMODEMToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.receiveFilesViaYMODEMToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.receiveFilesViaYMODEMGToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.sendFilesViaZModemToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.receiveFilesViaZModemToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.autoReceiveZModemToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.escapeZModemControlToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.sendFilesViaKermitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.receiveFilesViaKermitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
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
            this.toolStrip1.SuspendLayout();
            this.contextMenuStrip1.SuspendLayout();
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
            Icons.Apply(this.btnDisconnect, "terminal-disconnect");
            this.btnDisconnect.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.btnDisconnect.Name = "btnDisconnect";
            this.btnDisconnect.Size = new System.Drawing.Size(23, 24);
            this.btnDisconnect.Text = "Disconnect";
            // 
            // btnReconnect
            // 
            this.btnReconnect.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            Icons.Apply(this.btnReconnect, "terminal-reconnect");
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
            Icons.Apply(this.btnSend, "terminal-send");
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
            Icons.Apply(this.toolStripButton1, "terminal-clear");
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
            this.sendFilesViaYMODEMToolStripMenuItem,
            this.receiveFilesViaYMODEMToolStripMenuItem,
            this.receiveFilesViaYMODEMGToolStripMenuItem,
            this.sendFilesViaZModemToolStripMenuItem,
            this.receiveFilesViaZModemToolStripMenuItem,
            this.autoReceiveZModemToolStripMenuItem,
            this.escapeZModemControlToolStripMenuItem,
            this.sendFilesViaKermitToolStripMenuItem,
            this.receiveFilesViaKermitToolStripMenuItem});
            Icons.Apply(this.toolStripButton3, "terminal-transfer");
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
            // sendFilesViaYMODEMToolStripMenuItem
            // 
            this.sendFilesViaYMODEMToolStripMenuItem.Name = "sendFilesViaYMODEMToolStripMenuItem";
            this.sendFilesViaYMODEMToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.sendFilesViaYMODEMToolStripMenuItem.Text = "Send file(s) via YMODEM...";
            this.sendFilesViaYMODEMToolStripMenuItem.ToolTipText = "The receiver chooses the variant: YMODEM (CRC-16), the checksum variant or YMODEM-G (streaming).";
            // 
            // receiveFilesViaYMODEMToolStripMenuItem
            // 
            this.receiveFilesViaYMODEMToolStripMenuItem.Name = "receiveFilesViaYMODEMToolStripMenuItem";
            this.receiveFilesViaYMODEMToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.receiveFilesViaYMODEMToolStripMenuItem.Text = "Receive file(s) via YMODEM...";
            this.receiveFilesViaYMODEMToolStripMenuItem.ToolTipText = "Asks for CRC-16 and falls back to the checksum variant when the sender does not answer.";
            // 
            // receiveFilesViaYMODEMGToolStripMenuItem
            // 
            this.receiveFilesViaYMODEMGToolStripMenuItem.Name = "receiveFilesViaYMODEMGToolStripMenuItem";
            this.receiveFilesViaYMODEMGToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.receiveFilesViaYMODEMGToolStripMenuItem.Text = "Receive file(s) via YMODEM-G...";
            this.receiveFilesViaYMODEMGToolStripMenuItem.ToolTipText = "Streaming without acknowledgements: for error-free links only; a damaged block cancels the transfer.";
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
            // sendFilesViaKermitToolStripMenuItem
            // 
            this.sendFilesViaKermitToolStripMenuItem.Name = "sendFilesViaKermitToolStripMenuItem";
            this.sendFilesViaKermitToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.sendFilesViaKermitToolStripMenuItem.Text = "Send files via Kermit...";
            // 
            // receiveFilesViaKermitToolStripMenuItem
            // 
            this.receiveFilesViaKermitToolStripMenuItem.Name = "receiveFilesViaKermitToolStripMenuItem";
            this.receiveFilesViaKermitToolStripMenuItem.Size = new System.Drawing.Size(219, 22);
            this.receiveFilesViaKermitToolStripMenuItem.Text = "Receive files via Kermit...";
            // 
            // toolStripButton2
            // 
            this.toolStripButton2.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            Icons.Apply(this.toolStripButton2, "terminal-save");
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
            Icons.Apply(this.toolStripDropDownButton1, "terminal-settings");
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
            Icons.Apply(this.copyToolStripMenuItem, "copy");
            this.copyToolStripMenuItem.Name = "copyToolStripMenuItem";
            this.copyToolStripMenuItem.ShortcutKeyDisplayString = "Ctrl+Shift+C";
            this.copyToolStripMenuItem.Size = new System.Drawing.Size(176, 22);
            this.copyToolStripMenuItem.Text = "Copy";
            // 
            // copyAllToolStripMenuItem
            // 
            Icons.Apply(this.copyAllToolStripMenuItem, "copy");
            this.copyAllToolStripMenuItem.Name = "copyAllToolStripMenuItem";
            this.copyAllToolStripMenuItem.Size = new System.Drawing.Size(176, 22);
            this.copyAllToolStripMenuItem.Text = "Copy all";
            // 
            // pasteToolStripMenuItem
            // 
            Icons.Apply(this.pasteToolStripMenuItem, "paste");
            this.pasteToolStripMenuItem.Name = "pasteToolStripMenuItem";
            this.pasteToolStripMenuItem.ShortcutKeyDisplayString = "Ctrl+Shift+V";
            this.pasteToolStripMenuItem.Size = new System.Drawing.Size(176, 22);
            this.pasteToolStripMenuItem.Text = "Paste";
            this.btnDisconnect.Click += new System.EventHandler(this.btnDisconnect_Click);
            this.btnReconnect.Click += new System.EventHandler(this.btnReconnect_Click);
            this.btnSend.ButtonClick += new System.EventHandler(this.btnSend_ButtonClick);
            this.sendAsHEXToolStripMenuItem.Click += new System.EventHandler(this.sendAsHEXToolStripMenuItem_Click);
            this.sendImmediatelyToolStripMenuItem.Click += new System.EventHandler(this.sendImmediatelyToolStripMenuItem_Click);
            this.txtHexInput.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtHexInput_KeyDown);
            this.txtHexInput.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txtHexInput_KeyPress);
            this.txtHexInput.TextChanged += new System.EventHandler(this.txtHexInput_TextChanged);
            this.toolStripButton1.Click += new System.EventHandler(this.toolStripButton1_Click);
            this.toolStripButton3.ButtonClick += new System.EventHandler(this.toolStripButton3_Click);
            this.sendBinaryFileToolStripMenuItem.Click += new System.EventHandler(this.toolStripButton3_Click);
            this.sendFileViaXMODEMToolStripMenuItem.Click += new System.EventHandler(this.sendFileViaXMODEMToolStripMenuItem_Click);
            this.sendFileViaXMODEM1KToolStripMenuItem.Click += new System.EventHandler(this.sendFileViaXMODEM1KToolStripMenuItem_Click);
            this.receiveFileViaXMODEMToolStripMenuItem.Click += new System.EventHandler(this.receiveFileViaXMODEMToolStripMenuItem_Click);
            this.toolStripButton2.Click += new System.EventHandler(this.toolStripButton2_Click);
            this.btnLog.Click += new System.EventHandler(this.btnLog_Click);
            this.btnReplay.Click += new System.EventHandler(this.btnReplay_Click);
            this.cbASCII.Click += new System.EventHandler(this.cbASCII_Click);
            this.cbHEX.Click += new System.EventHandler(this.cbHEX_Click);
            this.cbBoth.Click += new System.EventHandler(this.cbBoth_Click);
            this.echoTypedCharactersToolStripMenuItem.Click += new System.EventHandler(this.echoTypedCharactersToolStripMenuItem_Click);
            this.changeSettingsToolStripMenuItem.Click += new System.EventHandler(this.changeSettingsToolStripMenuItem_Click);
            this.cbBaudRate.SelectedIndexChanged += new System.EventHandler(this.cbBaudRate_SelectedIndexChanged);
            this.cbBaudRate.Leave += new System.EventHandler(this.cbBaudRate_Leave);
            this.cbBaudRate.KeyDown += new System.Windows.Forms.KeyEventHandler(this.cbBaudRate_KeyDown);
            this.cbEncoding.SelectedIndexChanged += new System.EventHandler(this.cbEncoding_SelectedIndexChanged);
            this.contextMenuStrip1.Opening += new System.ComponentModel.CancelEventHandler(this.contextMenuStrip1_Opening);
            this.copyToolStripMenuItem.Click += new System.EventHandler(this.copyToolStripMenuItem_Click);
            this.copyAllToolStripMenuItem.Click += new System.EventHandler(this.copyAllToolStripMenuItem_Click);
            this.pasteToolStripMenuItem.Click += new System.EventHandler(this.pasteToolStripMenuItem_Click);
            this.toolStrip1.ResumeLayout(false);
            this.toolStrip1.PerformLayout();
            this.contextMenuStrip1.ResumeLayout(false);

	}

}
