using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class SerialPortSettingsForm 
{
	private IContainer components = null;

	private RadioButton rbDefault;

	private RadioButton rbManual;

	private Panel panel1;

	private Label label1;

	private ComboBox cbBitsPerSecond;

	private ComboBox cbFlowControl;

	private ComboBox cbStopBits;

	private Label label5;

	private ComboBox cbParity;

	private Label label4;

	private ComboBox cbDataBits;

	private Label label3;

	private Label label2;

	private Label label7;

	private Label label6;

	private CheckBox cbDtrOnOpen;

	private CheckBox cbRtsOnOpen;

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
		rbDefault = new RadioButton();
		rbManual = new RadioButton();
		panel1 = new Panel();
		label7 = new Label();
		label6 = new Label();
		cbFlowControl = new ComboBox();
		cbStopBits = new ComboBox();
		label5 = new Label();
		cbParity = new ComboBox();
		label4 = new Label();
		cbDataBits = new ComboBox();
		label3 = new Label();
		cbBitsPerSecond = new ComboBox();
		label2 = new Label();
		label1 = new Label();
		cbDtrOnOpen = new CheckBox();
		cbRtsOnOpen = new CheckBox();
		panel1.SuspendLayout();
		SuspendLayout();
		rbDefault.AutoSize = true;
		rbDefault.Location = new Point(12, 12);
		rbDefault.Name = "rbDefault";
		rbDefault.Size = new Size(136, 17);
		rbDefault.TabIndex = 1;
		rbDefault.TabStop = true;
		rbDefault.Text = "Use default settings for ";
		rbDefault.UseVisualStyleBackColor = true;
		rbManual.AutoSize = true;
		rbManual.Location = new Point(12, 35);
		rbManual.Name = "rbManual";
		rbManual.Size = new Size(194, 17);
		rbManual.TabIndex = 2;
		rbManual.TabStop = true;
		rbManual.Text = "Specify serial port settings manually:";
		rbManual.UseVisualStyleBackColor = true;
		rbManual.CheckedChanged += rbManual_CheckedChanged;
		panel1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		panel1.Controls.Add(cbRtsOnOpen);
		panel1.Controls.Add(cbDtrOnOpen);
		panel1.Controls.Add(label7);
		panel1.Controls.Add(label6);
		panel1.Controls.Add(cbFlowControl);
		panel1.Controls.Add(cbStopBits);
		panel1.Controls.Add(label5);
		panel1.Controls.Add(cbParity);
		panel1.Controls.Add(label4);
		panel1.Controls.Add(cbDataBits);
		panel1.Controls.Add(label3);
		panel1.Controls.Add(cbBitsPerSecond);
		panel1.Controls.Add(label2);
		panel1.Controls.Add(label1);
		panel1.Enabled = false;
		panel1.Location = new Point(31, 58);
		panel1.Name = "panel1";
		panel1.Size = new Size(332, 238);
		panel1.TabIndex = 1;
		label7.AutoSize = true;
		label7.Font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold, GraphicsUnit.Point, 0);
		label7.Location = new Point(190, 145);
		cbDtrOnOpen.AutoSize = true;
		cbDtrOnOpen.Checked = true;
		cbDtrOnOpen.CheckState = CheckState.Checked;
		cbDtrOnOpen.Location = new Point(15, 170);
		cbDtrOnOpen.Name = "cbDtrOnOpen";
		cbDtrOnOpen.Size = new Size(190, 17);
		cbDtrOnOpen.TabIndex = 105;
		cbDtrOnOpen.Text = "Raise DTR when the port is opened";
		cbDtrOnOpen.UseVisualStyleBackColor = true;
		cbRtsOnOpen.AutoSize = true;
		cbRtsOnOpen.Checked = true;
		cbRtsOnOpen.CheckState = CheckState.Checked;
		cbRtsOnOpen.Location = new Point(15, 193);
		cbRtsOnOpen.Name = "cbRtsOnOpen";
		cbRtsOnOpen.Size = new Size(190, 17);
		cbRtsOnOpen.TabIndex = 106;
		cbRtsOnOpen.Text = "Raise RTS when the port is opened";
		cbRtsOnOpen.UseVisualStyleBackColor = true;
		label7.Name = "label7";
		label7.Size = new Size(58, 13);
		label7.TabIndex = 3;
		label7.Text = "unknown";
		label6.AutoSize = true;
		label6.Location = new Point(98, 145);
		label6.Name = "label6";
		label6.Size = new Size(86, 13);
		label6.TabIndex = 3;
		label6.Text = "Maximum speed:";
		cbFlowControl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		cbFlowControl.DropDownStyle = ComboBoxStyle.DropDownList;
		cbFlowControl.FormattingEnabled = true;
		cbFlowControl.Items.AddRange(new object[4] { "None", "Software (XON/XOFF)", "Hardware (RTS/CTS)", "Both" });
		cbFlowControl.Location = new Point(101, 114);
		cbFlowControl.Name = "cbFlowControl";
		cbFlowControl.Size = new Size(228, 21);
		cbFlowControl.TabIndex = 104;
		cbStopBits.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		cbStopBits.DropDownStyle = ComboBoxStyle.DropDownList;
		cbStopBits.FormattingEnabled = true;
		cbStopBits.Items.AddRange(new object[3] { "1", "1.5", "2" });
		cbStopBits.Location = new Point(101, 87);
		cbStopBits.Name = "cbStopBits";
		cbStopBits.Size = new Size(228, 21);
		cbStopBits.TabIndex = 103;
		cbStopBits.SelectedIndexChanged += cbStopBits_SelectedIndexChanged;
		label5.AutoSize = true;
		label5.Location = new Point(12, 117);
		label5.Name = "label5";
		label5.Size = new Size(67, 13);
		label5.TabIndex = 0;
		label5.Text = "Flow control:";
		cbParity.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		cbParity.DropDownStyle = ComboBoxStyle.DropDownList;
		cbParity.FormattingEnabled = true;
		cbParity.Items.AddRange(new object[5] { "None", "Odd", "Even", "Mark", "Space" });
		cbParity.Location = new Point(101, 60);
		cbParity.Name = "cbParity";
		cbParity.Size = new Size(228, 21);
		cbParity.TabIndex = 102;
		cbParity.SelectedIndexChanged += cbStopBits_SelectedIndexChanged;
		label4.AutoSize = true;
		label4.Location = new Point(12, 90);
		label4.Name = "label4";
		label4.Size = new Size(51, 13);
		label4.TabIndex = 0;
		label4.Text = "Stop bits:";
		cbDataBits.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		cbDataBits.DropDownStyle = ComboBoxStyle.DropDownList;
		cbDataBits.FormattingEnabled = true;
		cbDataBits.Items.AddRange(new object[4] { "5", "6", "7", "8" });
		cbDataBits.Location = new Point(101, 33);
		cbDataBits.Name = "cbDataBits";
		cbDataBits.Size = new Size(228, 21);
		cbDataBits.TabIndex = 101;
		cbDataBits.SelectedIndexChanged += cbStopBits_SelectedIndexChanged;
		label3.AutoSize = true;
		label3.Location = new Point(12, 63);
		label3.Name = "label3";
		label3.Size = new Size(36, 13);
		label3.TabIndex = 0;
		label3.Text = "Parity:";
		cbBitsPerSecond.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		cbBitsPerSecond.FormattingEnabled = true;
		cbBitsPerSecond.Items.AddRange(new object[8] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" });
		cbBitsPerSecond.Location = new Point(101, 6);
		cbBitsPerSecond.Name = "cbBitsPerSecond";
		cbBitsPerSecond.Size = new Size(228, 21);
		cbBitsPerSecond.TabIndex = 100;
		cbBitsPerSecond.TextUpdate += cbStopBits_SelectedIndexChanged;
		label2.AutoSize = true;
		label2.Location = new Point(12, 36);
		label2.Name = "label2";
		label2.Size = new Size(52, 13);
		label2.TabIndex = 0;
		label2.Text = "Data bits:";
		label1.AutoSize = true;
		label1.Location = new Point(12, 9);
		label1.Name = "label1";
		label1.Size = new Size(83, 13);
		label1.TabIndex = 0;
		label1.Text = "Bits per second:";
		AutoScaleDimensions = new SizeF(6f, 13f);
		ClientSize = new Size(375, 339);
		Controls.Add(panel1);
		Controls.Add(rbManual);
		Controls.Add(rbDefault);
		MinimumSize = new Size(391, 377);
		Name = "SerialPortSettingsForm";
		Text = "Serial port settings";
		Controls.SetChildIndex(rbDefault, 0);
		Controls.SetChildIndex(rbManual, 0);
		Controls.SetChildIndex(panel1, 0);
		panel1.ResumeLayout(performLayout: false);
		panel1.PerformLayout();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
