using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class LogReplayForm 
{
	private IContainer components = null;

	private Button button1;

	private TrackBar trackBar1;

	private Timer _timer;

	private Button button2;

	private Label label1;

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
		button1 = new Button();
		trackBar1 = new TrackBar();
		_timer = new Timer(components);
		button2 = new Button();
		label1 = new Label();
		((ISupportInitialize)trackBar1).BeginInit();
		SuspendLayout();
		button1.Location = new Point(12, 12);
		button1.Name = "button1";
		button1.Size = new Size(75, 23);
		button1.TabIndex = 0;
		button1.Text = "Pause";
		button1.UseVisualStyleBackColor = true;
		button1.Click += button1_Click;
		trackBar1.Location = new Point(122, 28);
		trackBar1.Maximum = 100;
		trackBar1.Name = "trackBar1";
		trackBar1.Size = new Size(324, 45);
		trackBar1.TabIndex = 1;
		_timer.Enabled = true;
		_timer.Tick += OnTimerTick;
		button2.Location = new Point(12, 50);
		button2.Name = "button2";
		button2.Size = new Size(75, 23);
		button2.TabIndex = 0;
		button2.Text = "Reset";
		button2.UseVisualStyleBackColor = true;
		button2.Click += button2_Click;
		label1.AutoSize = true;
		label1.Location = new Point(119, 12);
		label1.Name = "label1";
		label1.Size = new Size(75, 13);
		label1.TabIndex = 2;
		label1.Text = "Replay speed:";
		AutoScaleDimensions = new SizeF(6f, 13f);
		ClientSize = new Size(487, 85);
		Controls.Add(label1);
		Controls.Add(trackBar1);
		Controls.Add(button2);
		Controls.Add(button1);
		Name = "LogReplayForm";
		Text = "LogReplayForm";
		((ISupportInitialize)trackBar1).EndInit();
		ResumeLayout(performLayout: false);
		PerformLayout();
	}

}
