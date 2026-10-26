using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

/// <summary>
/// Asks whether text that is about to be pasted into a terminal should really go there: shows what it is (lines,
/// characters, a final line break), a preview, and what is dangerous in it.
/// </summary>
public sealed class PasteConfirmForm : Form
{
    private readonly CheckBox _dontAsk;

    /// <summary>True when the user asked not to be asked about multi-line pastes again.</summary>
    public bool DontAskAgain => _dontAsk.Visible && _dontAsk.Checked;

    public PasteConfirmForm(string summary, string preview, IList<string> dangers, bool offerDontAsk)
    {
        bool dangerous = dangers != null && dangers.Count > 0;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = SystemFonts.MessageBoxFont;
        Text = dangerous ? "Paste - dangerous commands" : "Paste into the terminal";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(620, 400);
        MinimumSize = new Size(420, 300);
        KeyPreview = true;

        TableLayoutPanel layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label summaryLabel = new Label
        {
            Text = summary,
            AutoSize = true,
            MaximumSize = new Size(590, 0),
            Margin = new Padding(0, 0, 0, 6)
        };
        layout.Controls.Add(summaryLabel, 0, 0);

        Label dangerLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(590, 0),
            ForeColor = Color.Firebrick,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
            Visible = dangerous,
            Text = dangerous ? "Warning: the text " + string.Join("; ", dangers) + "." : ""
        };
        layout.Controls.Add(dangerLabel, 0, 1);

        TextBox previewBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, Font.Size),
            Text = preview ?? "",
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText
        };
        layout.Controls.Add(previewBox, 0, 2);

        _dontAsk = new CheckBox
        {
            Text = "Do not ask again for multi-line pastes (changeable in Settings)",
            AutoSize = true,
            Visible = offerDontAsk,
            Margin = new Padding(0, 6, 0, 0)
        };
        layout.Controls.Add(_dontAsk, 0, 3);

        FlowLayoutPanel buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        Button paste = new Button { Text = "Paste", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(paste);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);

        CancelButton = cancel;
        // Enter must not paste dangerous text by accident.
        AcceptButton = dangerous ? cancel : paste;
        ActiveControl = dangerous ? cancel : paste;
    }
}
