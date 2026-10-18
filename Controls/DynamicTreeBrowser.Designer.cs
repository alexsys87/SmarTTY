using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class DynamicTreeBrowser 
{
	private IContainer components = null;

	private TreeView treeView1;

	private System.Windows.Forms.Timer _timer;

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
		treeView1 = new TreeView();
		_timer = new System.Windows.Forms.Timer(components);
		SuspendLayout();
		treeView1.Dock = DockStyle.Fill;
		treeView1.FullRowSelect = true;
		treeView1.HideSelection = false;
		treeView1.Location = new Point(0, 0);
		treeView1.Name = "treeView1";
		treeView1.Size = new Size(762, 483);
		treeView1.TabIndex = 2;
		treeView1.AfterExpand += treeView1_AfterExpand;
		treeView1.NodeMouseClick += treeView1_NodeMouseClick;
		treeView1.NodeMouseDoubleClick += treeView1_NodeMouseDoubleClick;
		treeView1.KeyDown += treeView1_KeyDown;
		_timer.Tick += OnTimerTick;
		AutoScaleDimensions = new SizeF(6f, 13f);
		AutoScaleMode = AutoScaleMode.Font;
		Controls.Add(treeView1);
		Name = "DynamicTreeBrowser";
		Size = new Size(762, 483);
		ResumeLayout(performLayout: false);
	}

}
