using SmarTTY.IO;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Forms;

/// <summary>
/// The "Permissions..." dialog of the file list: read, write and execute for the owner, the group and the others, the
/// setuid, setgid and sticky bits, the octal form, and whether the change reaches into the selected folders. Built in code
/// so it needs no designer file.
/// </summary>
internal sealed class RemotePermissionsDialog : DpiAwareForm
{
    /// <summary>What the user asked for: the bits to switch and how far the change reaches.</summary>
    public sealed class Request
    {
        public RemotePermissionChange Change;

        public ChmodScope Scope;
    }

    // The twelve check boxes, in the order of these masks: owner, group and others (read, write, execute), then the special bits.
    private static readonly int[] Masks =
    {
        RemotePermissions.OwnerRead, RemotePermissions.OwnerWrite, RemotePermissions.OwnerExecute,
        RemotePermissions.GroupRead, RemotePermissions.GroupWrite, RemotePermissions.GroupExecute,
        RemotePermissions.OtherRead, RemotePermissions.OtherWrite, RemotePermissions.OtherExecute,
        RemotePermissions.SetUid, RemotePermissions.SetGid, RemotePermissions.Sticky
    };

    private readonly CheckBox[] _boxes = new CheckBox[Masks.Length];

    private readonly CheckState[] _initial = new CheckState[Masks.Length];

    private readonly TextBox _octal;

    private readonly CheckBox _recursive;

    private readonly ComboBox _scope;

    private bool _updating;

    /// <param name="modes">The modes of the selected items; null for an item whose mode is not known.</param>
    /// <param name="anyFolder">True if a folder is selected: only then can the change reach into the contents.</param>
    public RemotePermissionsDialog(string prompt, int?[] modes, bool anyFolder)
    {
        // Laid out for 96 DPI: DpiAwareForm scales it for the monitor it is on.
        FormIcons.Apply(this, "remote-file");
        Text = "Permissions";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(380, 342);

        Controls.Add(new Label { Text = prompt, AutoSize = false, Location = new Point(12, 12), Size = new Size(356, 20) });

        string[] columns = { "Owner", "Group", "Others" };
        string[] rows = { "Read", "Write", "Execute" };
        for (int column = 0; column < 3; column++)
        {
            Controls.Add(new Label { Text = columns[column], AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Location = new Point(100 + 90 * column, 40), Size = new Size(70, 20) });
        }
        for (int row = 0; row < 3; row++)
        {
            Controls.Add(new Label { Text = rows[row], AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(16, 64 + 26 * row), Size = new Size(80, 22) });
        }
        for (int index = 0; index < 9; index++)
        {
            // Index runs through read, write, execute of the owner, then of the group, then of the others.
            int column = index / 3;
            int row = index % 3;
            _boxes[index] = NewBox("", new Point(100 + 90 * column + 25, 64 + 26 * row), new Size(20, 22));
        }
        string[] specialNames = { "Set user ID", "Set group ID", "Sticky bit" };
        for (int index = 0; index < 3; index++)
        {
            _boxes[9 + index] = NewBox(specialNames[index], new Point(16 + 120 * index, 148), new Size(116, 22));
        }

        Controls.Add(new Label { Text = "Octal:", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(16, 182), Size = new Size(50, 22) });
        _octal = new TextBox { MaxLength = 4, Location = new Point(70, 182), Size = new Size(60, 24) };
        Controls.Add(_octal);

        _recursive = new CheckBox { Text = "Also change what is inside the selected folders", AutoSize = false, Location = new Point(16, 216), Size = new Size(350, 22), Enabled = anyFolder };
        _scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(40, 242), Size = new Size(200, 24), Enabled = false };
        _scope.Items.AddRange(new object[] { "Files and folders", "Files only", "Folders only" });
        _scope.SelectedIndex = 0;
        Controls.Add(_recursive);
        Controls.Add(_scope);
        Controls.Add(new Label { Text = "Only the permissions you change are applied to what is inside the folders; the others stay as they are.", AutoSize = false, ForeColor = SystemColors.GrayText, Location = new Point(16, 272), Size = new Size(352, 32) });

        Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(80, 26), Location = new Point(198, 310) };
        Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(80, 26), Location = new Point(288, 310) };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        // A bit is on or off in the boxes when it is so for all the selected items, and "mixed" (a third state that leaves it
        // as it is) when they differ or a mode is unknown.
        for (int index = 0; index < Masks.Length; index++)
        {
            int on = 0;
            int off = 0;
            foreach (int? mode in modes)
            {
                if (!mode.HasValue)
                {
                    on++;
                    off++;
                }
                else if ((mode.Value & Masks[index]) != 0)
                {
                    on++;
                }
                else
                {
                    off++;
                }
            }
            _initial[index] = on > 0 && off > 0 ? CheckState.Indeterminate : on > 0 ? CheckState.Checked : CheckState.Unchecked;
            _boxes[index].ThreeState = _initial[index] == CheckState.Indeterminate;
            _boxes[index].CheckState = _initial[index];
        }
        UpdateOctal();

        foreach (CheckBox box in _boxes)
        {
            box.CheckStateChanged += (sender, e) => UpdateOctal();
        }
        _octal.TextChanged += (sender, e) => ApplyOctal();
        _recursive.CheckedChanged += (sender, e) => _scope.Enabled = _recursive.Checked;
    }

    private CheckBox NewBox(string text, Point location, Size size)
    {
        CheckBox box = new CheckBox { Text = text, AutoSize = false, Location = location, Size = size };
        Controls.Add(box);
        return box;
    }

    // The octal text follows the boxes, as long as every bit is on or off.
    private void UpdateOctal()
    {
        if (_updating)
        {
            return;
        }
        int mode = 0;
        for (int index = 0; index < Masks.Length; index++)
        {
            if (_boxes[index].CheckState == CheckState.Indeterminate)
            {
                SetOctalText("");
                return;
            }
            if (_boxes[index].CheckState == CheckState.Checked)
            {
                mode |= Masks[index];
            }
        }
        SetOctalText(RemotePermissions.ToOctal(mode));
    }

    private void SetOctalText(string text)
    {
        _updating = true;
        try
        {
            _octal.Text = text;
        }
        finally
        {
            _updating = false;
        }
    }

    // Typing a number sets the boxes (a mode of up to three digits has no special bits, so those boxes go off).
    private void ApplyOctal()
    {
        if (_updating || !RemotePermissions.TryParseOctal(_octal.Text, out int mode))
        {
            return;
        }
        _updating = true;
        try
        {
            for (int index = 0; index < Masks.Length; index++)
            {
                _boxes[index].CheckState = (mode & Masks[index]) != 0 ? CheckState.Checked : CheckState.Unchecked;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    // The bits whose box differs from what the items had: those are the ones to switch.
    private Request GetRequest()
    {
        RemotePermissionChange change = new RemotePermissionChange();
        for (int index = 0; index < Masks.Length; index++)
        {
            CheckState state = _boxes[index].CheckState;
            if (state == _initial[index] || state == CheckState.Indeterminate)
            {
                continue;
            }
            if (state == CheckState.Checked)
            {
                change.SetBits |= Masks[index];
            }
            else
            {
                change.ClearBits |= Masks[index];
            }
        }
        ChmodScope scope = ChmodScope.Selected;
        if (_recursive.Checked)
        {
            scope = _scope.SelectedIndex == 1 ? ChmodScope.RecursiveFiles : _scope.SelectedIndex == 2 ? ChmodScope.RecursiveFolders : ChmodScope.Recursive;
        }
        return new Request { Change = change, Scope = scope };
    }

    /// <summary>Shows the dialog; returns what to change, or null if it was cancelled or nothing was changed.</summary>
    public static Request Ask(string prompt, int?[] modes, bool anyFolder)
    {
        using RemotePermissionsDialog dialog = new RemotePermissionsDialog(prompt, modes, anyFolder);
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return null;
        }
        Request request = dialog.GetRequest();
        return request.Change.IsEmpty ? null : request;
    }
}
