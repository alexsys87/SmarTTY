using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

public partial class HexTerminalControl 
{
	private IContainer components = null;

	private TerminalEmulatorBase termHex;

	private TerminalEmulatorBase termText;

	private Timer _resizeTimer;

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
		HighlightStyle highlightStyle = new HighlightStyle();
		HighlightStyle highlightStyle2 = new HighlightStyle();
		termHex = new TerminalEmulatorBase();
		termText = new TerminalEmulatorBase();
		_resizeTimer = new Timer(components);
		SuspendLayout();
		termHex.ActiveBufferIndex = 0;
		termHex.AutoScroll = true;
		termHex.AutoWrap = false;
		termHex.BackColor = Color.Black;
		termHex.NormalTextColor = Color.White;
		termHex.CaretBounds = new Rectangle(0, 0, 0, 0);
		termHex.TerminalMode = true;
		termHex.Dock = DockStyle.Left;
		termHex.LayoutSuspended = false;
		termHex.Font = new Font("Courier New", 12f, FontStyle.Regular, GraphicsUnit.Point, 204);
		termHex.ForeColor = Color.LightGray;
		termHex.NavigationLocked = true;
		termHex.HideCaret = false;
		termHex.HideMargins = true;
		termHex.HorizontalScrollBarVisible = false;
		termHex.ShowLineNumbers = true;
		termHex.TopLine = 0;
		termHex.Location = new Point(0, 0);
		termHex.SelectionStyle = highlightStyle;
		termHex.Margin = new Padding(5, 4, 5, 4);
		termHex.Name = "termHex";
		((ReadonlyTextBrowser)termHex).OverwriteMode = false;
		termHex.ReadOnly = true;
		termHex.CaretWhenUnfocused = false;
		termHex.SingleLine = false;
		termHex.Size = new Size(481, 231);
		((ReadonlySmartScrollBrowser)termHex).SmartAutoScroll = false;
		termHex.TabIndex = 0;
		termHex.ScrollbackLimit = int.MaxValue;
		termHex.Title = null;
		termHex.IsModified = false;
		termHex.VerticalScrollBarVisible = false;
		((ReadonlyTextBrowser)termHex).SelectionChanged += SyncSelectionToText;
		termHex.Scroll += termHex_Scroll;
		termHex.KeyPress += termHex_KeyPress;
		termHex.KeyDown += termView_KeyDown;
		termText.ActiveBufferIndex = 0;
		termText.AutoScroll = true;
		termText.AutoWrap = false;
		termText.BackColor = Color.Black;
		termText.NormalTextColor = Color.White;
		termText.CaretBounds = new Rectangle(0, 0, 0, 0);
		termText.TerminalMode = true;
		termText.Dock = DockStyle.Right;
		termText.LayoutSuspended = false;
		termText.Font = new Font("Courier New", 12f, FontStyle.Regular, GraphicsUnit.Point, 204);
		termText.ForeColor = Color.LightGray;
		termText.NavigationLocked = true;
		termText.HideCaret = false;
		termText.HideMargins = true;
		termText.HorizontalScrollBarVisible = false;
		termText.ShowLineNumbers = true;
		termText.TopLine = 0;
		termText.Location = new Point(485, 0);
		termText.SelectionStyle = highlightStyle2;
		termText.Margin = new Padding(5, 4, 5, 4);
		termText.Name = "termText";
		((ReadonlyTextBrowser)termText).OverwriteMode = false;
		termText.ReadOnly = true;
		termText.CaretWhenUnfocused = false;
		termText.SingleLine = false;
		termText.Size = new Size(157, 231);
		((ReadonlySmartScrollBrowser)termText).SmartAutoScroll = false;
		termText.TabIndex = 0;
		termText.ScrollbackLimit = int.MaxValue;
		termText.Title = null;
		termText.IsModified = false;
		termText.VerticalScrollBarVisible = true;
		((ReadonlyTextBrowser)termText).SelectionChanged += SyncSelectionToHex;
		termText.Scroll += termText_Scroll;
		termText.KeyPress += termHex_KeyPress;
		termText.KeyDown += termView_KeyDown;
		_resizeTimer.Tick += OnResizeTimer;
		AutoScaleDimensions = new SizeF(6f, 13f);
		Controls.Add(termHex);
		Controls.Add(termText);
		Name = "HexTerminalControl";
		Size = new Size(642, 231);
		ResumeLayout(performLayout: false);
	}

}
