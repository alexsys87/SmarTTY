using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

internal sealed class HexDialogRequestedEventArgs : EventArgs
{
    public HexDialogRequestedEventArgs(HexDialogKind kind)
    {
        Kind = kind;
    }

    public HexDialogKind Kind { get; }
}

/// <summary>
/// The submenus the context menu of the file editor shows in the hex view, as WinForms menu items: the items and their rules are
/// <see cref="HexMenuModel"/> (marking bytes with colours, copying them as source code, filling, inserting and swapping bytes, checksums, and how
/// the bytes are shown); this class makes the items, shows the errors of a command and the colour swatches.
/// </summary>
internal sealed class HexEditorMenu : IDisposable
{
    private readonly HexMenuModel _model;

    private readonly Action _afterCommand;

    private readonly List<Image> _images = new List<Image>();

    private readonly List<ToolStripMenuItem> _menus = new List<ToolStripMenuItem>();

    // The item of every entry of the model that is not a separator.
    private readonly List<KeyValuePair<HexMenuItem, ToolStripMenuItem>> _items = new List<KeyValuePair<HexMenuItem, ToolStripMenuItem>>();

    public HexEditorMenu(IHexEditorView editor, Action<HexDialogKind> requestDialog, Action afterCommand)
    {
        if (editor == null)
        {
            throw new ArgumentNullException(nameof(editor));
        }
        _afterCommand = afterCommand ?? (() => { });
        _model = new HexMenuModel(editor, requestDialog,
            error => ThemedMessageBox.Show(error, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Asterisk));
        foreach (HexSubmenu submenu in _model.Submenus)
        {
            ToolStripMenuItem menu = new ToolStripMenuItem(submenu.Text);
            foreach (HexMenuItem entry in submenu.Items)
            {
                if (entry.IsSeparator)
                {
                    menu.DropDownItems.Add(new ToolStripSeparator());
                    continue;
                }
                // The key is shown only: the editor handles the key itself.
                ToolStripMenuItem item = new ToolStripMenuItem(entry.Text)
                {
                    ShortcutKeyDisplayString = entry.Shortcut
                };
                if (entry.Swatch.HasValue)
                {
                    item.Image = CreateSwatch(entry.Swatch.Value.ToColor());
                }
                HexMenuItem command = entry;
                item.Click += (sender, e) =>
                {
                    try
                    {
                        command.Run();
                    }
                    catch (Exception exception)
                    {
                        ThemedMessageBox.Show(exception.Message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                    _afterCommand();
                };
                menu.DropDownItems.Add(item);
                _items.Add(new KeyValuePair<HexMenuItem, ToolStripMenuItem>(entry, item));
            }
            _menus.Add(menu);
        }
    }

    /// <summary>The submenus, in the order they are shown.</summary>
    public IReadOnlyList<ToolStripMenuItem> Menus => _menus;

    /// <summary>The drop-downs of the submenus (to theme and scale them with the context menu).</summary>
    public IEnumerable<ToolStripDropDown> DropDowns
    {
        get
        {
            foreach (ToolStripMenuItem menu in Menus)
            {
                yield return menu.DropDown;
            }
        }
    }

    /// <summary>Shows the submenus in the hex view only and enables what the editor allows now.</summary>
    public void Refresh(bool hexMode)
    {
        foreach (ToolStripMenuItem menu in Menus)
        {
            menu.Visible = hexMode;
        }
        if (!hexMode)
        {
            return;
        }
        for (int i = 0; i < _menus.Count; i++)
        {
            _menus[i].Enabled = _model.Submenus[i].IsEnabled;
        }
        foreach (KeyValuePair<HexMenuItem, ToolStripMenuItem> pair in _items)
        {
            pair.Value.Enabled = pair.Key.IsEnabled;
            pair.Value.Checked = pair.Key.IsChecked;
        }
    }

    private Image CreateSwatch(Color color)
    {
        Bitmap bitmap = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using (SolidBrush brush = new SolidBrush(color))
            {
                graphics.FillRectangle(brush, 2, 2, 12, 12);
            }
            using (Pen pen = new Pen(Color.FromArgb(120, 0, 0, 0)))
            {
                graphics.DrawRectangle(pen, 2, 2, 11, 11);
            }
        }
        _images.Add(bitmap);
        return bitmap;
    }

    public void Dispose()
    {
        foreach (Image image in _images)
        {
            image.Dispose();
        }
        _images.Clear();
    }
}
