using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SmarTTY.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

/// <summary>
/// Editor for a user command list of the line-input mode: one command per line, lines starting with '#' are
/// comments. Built in code so it needs no designer file.
/// </summary>
internal sealed class DeviceCommandListDialog : DpiAwareForm
{
    private readonly TextBox _text;

    public DeviceCommandListDialog(string title, string description, IEnumerable<string> commands)
    {
        // Laid out for 96 DPI: DpiAwareForm scales it for the monitor it is on.
        FormIcons.Apply(this, "command-list");
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 340);
        MinimumSize = new Size(320, 240);

        Label hint = new Label
        {
            Text = description,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 56,
            Padding = new Padding(8, 8, 8, 0)
        };
        _text = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 10f),
            Text = CommandListFile.Format(commands).Replace("\r\n", "\n").Replace("\n", "\r\n")
        };
        Panel textHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        textHost.Controls.Add(_text);

        Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 26) };
        Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 26) };
        FlowLayoutPanel buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8, 0, 8, 8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(textHost);
        Controls.Add(buttons);
        Controls.Add(hint);
        CancelButton = cancel;
        // Enter belongs to the text box here (one command per line), so OK is reached with the mouse or Ctrl+Enter.
        KeyPreview = true;
        KeyDown += (sender, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Return)
            {
                e.SuppressKeyPress = true;
                DialogResult = DialogResult.OK;
            }
        };
    }

    /// <summary>The commands as edited (comments, blank lines and duplicates removed).</summary>
    public List<string> Commands => CommandListFile.Parse(_text.Text);
}
