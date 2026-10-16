using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SmarTTY.Controls;
using SmarTTY.TextEditing;

namespace SmarTTY.Forms;

public partial class SudoConfirmationForm 
{
	private IContainer components = null;

	private TerminalEmulatorControl terminalEmulatorControl1;

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
		HighlightStyle highlightStyle = new HighlightStyle();
		terminalEmulatorControl1 = new TerminalEmulatorControl();
		SuspendLayout();
		terminalEmulatorControl1.ActiveBufferIndex = 0;
		terminalEmulatorControl1.AutoScroll = true;
		terminalEmulatorControl1.AutoWrap = true;
		terminalEmulatorControl1.BackColor = Color.Black;
		terminalEmulatorControl1.BellMode = TerminalEmulatorControl.BeepMode.None;
		terminalEmulatorControl1.BellSoundFile = null;
		terminalEmulatorControl1.NormalTextColor = Color.White;
		terminalEmulatorControl1.CaptureDialogKeys = false;
		((ReadonlyTextBrowser)terminalEmulatorControl1).CaretBounds = new Rectangle(0, 0, 0, 0);
		((ReadonlyTextBrowser)terminalEmulatorControl1).TerminalMode = true;
		terminalEmulatorControl1.TabWidth = 8;
		terminalEmulatorControl1.Dock = DockStyle.Fill;
		terminalEmulatorControl1.LayoutSuspended = false;
		terminalEmulatorControl1.Font = new Font("Courier New", 12f, FontStyle.Regular, GraphicsUnit.Point, 204);
		terminalEmulatorControl1.ForeColor = Color.LightGray;
		terminalEmulatorControl1.NavigationLocked = true;
		terminalEmulatorControl1.HideCaret = false;
		terminalEmulatorControl1.HideMargins = true;
		terminalEmulatorControl1.HorizontalScrollBarVisible = false;
		terminalEmulatorControl1.ForwardAltF4 = false;
		terminalEmulatorControl1.ShowLineNumbers = true;
		terminalEmulatorControl1.TopLine = 0;
		terminalEmulatorControl1.Location = new Point(0, 0);
		terminalEmulatorControl1.SelectionStyle = highlightStyle;
		terminalEmulatorControl1.Margin = new Padding(5, 4, 5, 4);
		terminalEmulatorControl1.MouseMode = TerminalEmulatorControl.MouseReportingMode.Off;
		terminalEmulatorControl1.Name = "terminalEmulatorControl1";
		terminalEmulatorControl1.EnterSequence = null;
		((ReadonlyTextBrowser)terminalEmulatorControl1).OverwriteMode = false;
		((EditableTextBrowser)terminalEmulatorControl1).ReadOnly = true;
		terminalEmulatorControl1.CaretWhenUnfocused = true;
		terminalEmulatorControl1.ShowUnrecognizedAsHex = false;
		terminalEmulatorControl1.SingleLine = false;
		terminalEmulatorControl1.Size = new Size(440, 233);
		((ReadonlySmartScrollBrowser)terminalEmulatorControl1).SmartAutoScroll = false;
		terminalEmulatorControl1.TabIndex = 0;
		terminalEmulatorControl1.ScrollbackLimit = int.MaxValue;
		terminalEmulatorControl1.Title = null;
		terminalEmulatorControl1.IsModified = false;
		terminalEmulatorControl1.VerticalScrollBarVisible = false;
		terminalEmulatorControl1.Dispatcher = null;
		ClientSize = new Size(440, 233);
		Controls.Add(terminalEmulatorControl1);
		Name = "SudoConfirmationForm";
		Text = "Sudo confirmation";
		ResumeLayout(performLayout: false);
	}

}
