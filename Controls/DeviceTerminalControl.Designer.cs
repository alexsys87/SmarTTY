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

	private TableLayoutPanel pnlConnectionCard;

	private Label lblConnectionTitle;

	private PictureBox picConnectionState;

	private TableLayoutPanel pnlConnectionActions;

	private Button btnTryReconnect;

	private Button btnCloseTab;

	private Button btnIgnore;

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
            SmarTTY.TextEditing.HighlightStyle highlightStyle1 = new SmarTTY.TextEditing.HighlightStyle();
            this.pnlConnectionInProgress = new System.Windows.Forms.Panel();
            this.pnlConnectionCard = new System.Windows.Forms.TableLayoutPanel();
            this.lblConnectionTitle = new System.Windows.Forms.Label();
            this.picConnectionState = new System.Windows.Forms.PictureBox();
            this.pnlConnectionActions = new System.Windows.Forms.TableLayoutPanel();
            this.btnTryReconnect = new System.Windows.Forms.Button();
            this.btnCloseTab = new System.Windows.Forms.Button();
            this.btnIgnore = new System.Windows.Forms.Button();
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
            this.hexTerminalControl1 = new SmarTTY.Controls.HexTerminalControl();
            this.terminalEmulatorControl1 = new SmarTTY.Controls.TerminalEmulatorControl();
            ((System.ComponentModel.ISupportInitialize)(this.picConnectionState)).BeginInit();
            this.pnlConnectionActions.SuspendLayout();
            this.pnlConnectionCard.SuspendLayout();
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
            this.pnlConnectionInProgress.Controls.Add(this.pnlConnectionCard);
            this.pnlConnectionInProgress.Location = new System.Drawing.Point(0, 0);
            this.pnlConnectionInProgress.Name = "pnlConnectionInProgress";
            this.pnlConnectionInProgress.Size = new System.Drawing.Size(634, 236);
            this.pnlConnectionInProgress.TabIndex = 1;
            this.pnlConnectionInProgress.Visible = false;
            // 
            // pnlConnectionCard
            // 
            // The card of the "connection closed" prompt: the same content and order as the prompt of an SSH session
            // (title, text, picture and the three actions), coloured by ApplyConnectionPromptTheme.
            this.pnlConnectionCard.AutoSize = true;
            this.pnlConnectionCard.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.pnlConnectionCard.ColumnCount = 2;
            this.pnlConnectionCard.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 80F));
            this.pnlConnectionCard.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 340F));
            this.pnlConnectionCard.Controls.Add(this.lblConnectionTitle, 0, 0);
            this.pnlConnectionCard.Controls.Add(this.lblConnecting, 0, 1);
            this.pnlConnectionCard.Controls.Add(this.picConnectionState, 0, 2);
            this.pnlConnectionCard.Controls.Add(this.pnlConnectionActions, 1, 2);
            this.pnlConnectionCard.Location = new System.Drawing.Point(107, 40);
            this.pnlConnectionCard.Name = "pnlConnectionCard";
            this.pnlConnectionCard.RowCount = 3;
            this.pnlConnectionCard.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.pnlConnectionCard.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.pnlConnectionCard.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.pnlConnectionCard.Size = new System.Drawing.Size(420, 156);
            this.pnlConnectionCard.TabIndex = 0;
            // 
            // lblConnectionTitle
            // 
            this.lblConnectionTitle.AutoSize = true;
            this.lblConnectionTitle.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.pnlConnectionCard.SetColumnSpan(this.lblConnectionTitle, 2);
            this.lblConnectionTitle.Font = new System.Drawing.Font("Segoe UI", 18F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblConnectionTitle.Margin = new System.Windows.Forms.Padding(3, 0, 3, 6);
            this.lblConnectionTitle.Name = "lblConnectionTitle";
            this.lblConnectionTitle.TabIndex = 0;
            this.lblConnectionTitle.Text = "Connecting...";
            this.lblConnectionTitle.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // lblConnecting
            // 
            this.lblConnecting.AutoSize = true;
            this.lblConnecting.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.pnlConnectionCard.SetColumnSpan(this.lblConnecting, 2);
            this.lblConnecting.Margin = new System.Windows.Forms.Padding(3, 0, 3, 6);
            this.lblConnecting.MaximumSize = new System.Drawing.Size(414, 0);
            this.lblConnecting.Name = "lblConnecting";
            this.lblConnecting.TabIndex = 1;
            this.lblConnecting.Text = "Connecting...";
            // 
            // picConnectionState
            // 
            this.picConnectionState.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.picConnectionState.Margin = new System.Windows.Forms.Padding(8);
            this.picConnectionState.Name = "picConnectionState";
            this.picConnectionState.Size = new System.Drawing.Size(64, 64);
            this.picConnectionState.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.picConnectionState.TabIndex = 2;
            this.picConnectionState.TabStop = false;
            // 
            // pnlConnectionActions
            // 
            this.pnlConnectionActions.AutoSize = true;
            this.pnlConnectionActions.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.pnlConnectionActions.ColumnCount = 1;
            this.pnlConnectionActions.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.pnlConnectionActions.Controls.Add(this.btnTryReconnect, 0, 0);
            this.pnlConnectionActions.Controls.Add(this.btnCloseTab, 0, 1);
            this.pnlConnectionActions.Controls.Add(this.btnIgnore, 0, 2);
            this.pnlConnectionActions.Controls.Add(this.progressBar1, 0, 3);
            this.pnlConnectionActions.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlConnectionActions.Margin = new System.Windows.Forms.Padding(0);
            this.pnlConnectionActions.Name = "pnlConnectionActions";
            this.pnlConnectionActions.RowCount = 4;
            this.pnlConnectionActions.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.pnlConnectionActions.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.pnlConnectionActions.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.pnlConnectionActions.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.pnlConnectionActions.TabIndex = 3;
            // 
            // btnTryReconnect
            // 
            this.btnTryReconnect.Dock = System.Windows.Forms.DockStyle.Fill;
            this.btnTryReconnect.Margin = new System.Windows.Forms.Padding(3);
            this.btnTryReconnect.MinimumSize = new System.Drawing.Size(0, 26);
            this.btnTryReconnect.Name = "btnTryReconnect";
            this.btnTryReconnect.TabIndex = 0;
            this.btnTryReconnect.Text = "Try reconnecting";
            this.btnTryReconnect.UseVisualStyleBackColor = true;
            // 
            // btnCloseTab
            // 
            this.btnCloseTab.Dock = System.Windows.Forms.DockStyle.Fill;
            this.btnCloseTab.Margin = new System.Windows.Forms.Padding(3);
            this.btnCloseTab.MinimumSize = new System.Drawing.Size(0, 26);
            this.btnCloseTab.Name = "btnCloseTab";
            this.btnCloseTab.TabIndex = 1;
            this.btnCloseTab.Text = "Close tab";
            this.btnCloseTab.UseVisualStyleBackColor = true;
            // 
            // btnIgnore
            // 
            this.btnIgnore.Dock = System.Windows.Forms.DockStyle.Fill;
            this.btnIgnore.Margin = new System.Windows.Forms.Padding(3);
            this.btnIgnore.MinimumSize = new System.Drawing.Size(0, 26);
            this.btnIgnore.Name = "btnIgnore";
            this.btnIgnore.TabIndex = 2;
            this.btnIgnore.Text = "Ignore";
            this.btnIgnore.UseVisualStyleBackColor = true;
            // 
            // progressBar1
            // 
            this.progressBar1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.progressBar1.Margin = new System.Windows.Forms.Padding(3, 8, 3, 3);
            this.progressBar1.Name = "progressBar1";
            this.progressBar1.Size = new System.Drawing.Size(334, 18);
            this.progressBar1.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            this.progressBar1.TabIndex = 3;
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
            // Event handlers (YMODEM, ZMODEM and Kermit items are wired in their own partial files)
            //
            this.btnTryReconnect.Click += new System.EventHandler(this.btnTryReconnect_Click);
            this.btnCloseTab.Click += new System.EventHandler(this.btnCloseTab_Click);
            this.btnIgnore.Click += new System.EventHandler(this.btnIgnore_Click);
            this.pnlConnectionInProgress.Resize += new System.EventHandler(this.pnlConnectionInProgress_Resize);
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
            this.hexTerminalControl1.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.hexTerminalControl1_KeyPress);
            this.hexTerminalControl1.KeyDown += new System.Windows.Forms.KeyEventHandler(this.OnTerminalKeyDown);
            this.terminalEmulatorControl1.DataEntered += new SmarTTY.Controls.TerminalEmulatorControl.TerminalDataHandler(this.OnTerminalDataEntered);
            this.terminalEmulatorControl1.KeyDown += new System.Windows.Forms.KeyEventHandler(this.OnTerminalKeyDown);
            // 
            // DeviceTerminalControl
            // 
            this.Controls.Add(this.toolStrip1);
            this.Controls.Add(this.hexTerminalControl1);
            this.Controls.Add(this.terminalEmulatorControl1);
            this.Controls.Add(this.pnlConnectionInProgress);
            this.Name = "DeviceTerminalControl";
            this.Size = new System.Drawing.Size(634, 236);
            ((System.ComponentModel.ISupportInitialize)(this.picConnectionState)).EndInit();
            this.pnlConnectionActions.ResumeLayout(false);
            this.pnlConnectionActions.PerformLayout();
            this.pnlConnectionCard.ResumeLayout(false);
            this.pnlConnectionCard.PerformLayout();
            this.pnlConnectionInProgress.ResumeLayout(false);
            this.pnlConnectionInProgress.PerformLayout();
            this.toolStrip1.ResumeLayout(false);
            this.toolStrip1.PerformLayout();
            this.contextMenuStrip1.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

	}

}
