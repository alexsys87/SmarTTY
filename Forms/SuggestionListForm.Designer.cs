using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SmarTTY.Controls;

namespace SmarTTY.Forms;

internal partial class SuggestionListForm 
{
	private IContainer components = null;

	private SuggestionListView listView1;

	private ColumnHeader _columnHeader;

	private ColumnHeader _columnHeader2;

	private ImageList _imageList;

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
		listView1 = new SuggestionListView();
		_columnHeader = new ColumnHeader();
		_columnHeader2 = new ColumnHeader();
		_imageList = new ImageList(components);
		SuspendLayout();
		listView1.Columns.AddRange(new ColumnHeader[2] { _columnHeader, _columnHeader2 });
		listView1.Dock = DockStyle.Fill;
		listView1.FullRowSelect = true;
		listView1.HeaderStyle = ColumnHeaderStyle.None;
		listView1.HideSelection = false;
		listView1.Location = new Point(0, 0);
		listView1.MultiSelect = false;
		listView1.Name = "listView1";
		listView1.Size = new Size(386, 215);
		listView1.Sorting = SortOrder.Ascending;
		listView1.StateImageList = _imageList;
		listView1.TabIndex = 0;
		listView1.UseCompatibleStateImageBehavior = false;
		listView1.View = View.Details;
		listView1.SelectedIndexChanged += listView1_SelectedIndexChanged;
		_columnHeader.Text = "Command";
		_columnHeader.Width = 130;
		_columnHeader2.Text = "Description";
		_columnHeader2.Width = 200;
		_imageList.ColorDepth = ColorDepth.Depth24Bit;
		_imageList.ImageSize = new Size(16, 16);
		_imageList.TransparentColor = Color.Transparent;
		AutoScaleDimensions = new SizeF(6f, 13f);
		AutoScaleMode = AutoScaleMode.Font;
		ClientSize = new Size(386, 215);
		Controls.Add(listView1);
		FormBorderStyle = FormBorderStyle.None;
		Name = "SuggestionListForm";
		ShowIcon = false;
		// A completion popup must not add a button to the taskbar.
		ShowInTaskbar = false;
		Text = "SuggestionList";
		Deactivate += SuggestionListForm_Deactivate;
		Activated += SuggestionListForm_Activated;
		VisibleChanged += SuggestionListForm_VisibleChanged;
		FormClosed += SuggestionListForm_FormClosed;
		ResumeLayout(performLayout: false);
	}

}
