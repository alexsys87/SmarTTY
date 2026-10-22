using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

/// <summary>
/// Asks for the name of a new or renamed file or folder in the file list. Built in code so it needs no designer file.
/// </summary>
internal sealed class RemoteNameDialog : DpiAwareForm
{
    private readonly TextBox _name;

    public string EnteredName => _name.Text;

    public RemoteNameDialog(string title, string prompt, string initialName, bool selectStemOnly)
    {
        // Laid out for 96 DPI: DpiAwareForm scales it for the monitor it is on.
        FormIcons.Apply(this, "rename");
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(400, 112);

        Label label = new Label
        {
            Text = prompt,
            AutoSize = false,
            Location = new Point(12, 12),
            Size = new Size(376, 20)
        };
        _name = new TextBox
        {
            Text = initialName ?? "",
            Location = new Point(12, 36),
            Size = new Size(376, 24),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
        };
        Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(80, 26), Location = new Point(222, 74) };
        Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(80, 26), Location = new Point(308, 74) };
        Controls.Add(label);
        Controls.Add(_name);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        // As in Explorer, renaming "report.txt" selects "report" so that typing keeps the extension.
        Shown += (sender, e) =>
        {
            _name.Focus();
            int stemLength = _name.Text.Length;
            if (selectStemOnly)
            {
                int dot = _name.Text.LastIndexOf('.');
                if (dot > 0)
                {
                    stemLength = dot;
                }
            }
            _name.Select(0, stemLength);
        };
    }

    /// <summary>Shows the dialog; returns the entered name, or null if it was cancelled.</summary>
    public static string Ask(string title, string prompt, string initialName, bool selectStemOnly = false)
    {
        using RemoteNameDialog dialog = new RemoteNameDialog(title, prompt, initialName, selectStemOnly);
        return dialog.ShowDialog() == DialogResult.OK ? dialog.EnteredName : null;
    }
}
