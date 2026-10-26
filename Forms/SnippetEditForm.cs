using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

/// <summary>Adds or edits a snippet of the paste menu: a name and the text. Delete closes it with <see cref="DialogResult.Abort"/>.</summary>
public sealed class SnippetEditForm : Form
{
    private readonly TextBox _name;

    private readonly TextBox _text;

    public string SnippetName => _name.Text.Trim();

    public string SnippetText => _text.Text;

    public SnippetEditForm(string name, string text, bool canDelete)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = SystemFonts.MessageBoxFont;
        Text = canDelete ? "Edit snippet" : "New snippet";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 360);
        MinimumSize = new Size(380, 260);

        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "Name (shown in the paste menu):", AutoSize = true }, 0, 0);
        _name = new TextBox { Dock = DockStyle.Top, Text = name ?? "", Margin = new Padding(0, 2, 0, 8) };
        layout.Controls.Add(_name, 0, 1);
        layout.Controls.Add(new Label { Text = "Text to paste:", AutoSize = true }, 0, 2);
        _text = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = false,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, Font.Size),
            Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
            Margin = new Padding(0, 2, 0, 0)
        };
        layout.Controls.Add(_text, 0, 3);

        FlowLayoutPanel buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        Button save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 28) };
        save.Click += (sender, e) =>
        {
            if (_text.Text.Length == 0)
            {
                _text.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        if (canDelete)
        {
            Button delete = new Button { Text = "Delete", DialogResult = DialogResult.Abort, AutoSize = true, MinimumSize = new Size(90, 28) };
            buttons.Controls.Add(delete);
        }
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        CancelButton = cancel;
        ActiveControl = _name;
    }
}
