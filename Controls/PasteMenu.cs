using SmarTTY.Forms;
using SmarTTY.Native;
using SmarTTY.Terminal;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

/// <summary>
/// The paste menu of a terminal: the clipboard, the texts pasted lately (kept in memory only) and the snippets the user
/// saved. Every item is pasted through the terminal's own paste, so cleaning and confirmations apply as to any paste.
/// </summary>
internal static class PasteMenu
{
    private const int PreviewLength = 60;

    private const int ToolTipLength = 600;

    /// <summary>
    /// Shows the menu at <paramref name="location"/> (client coordinates of <paramref name="owner"/>; the mouse position
    /// when it is over the control, otherwise the top left corner).
    /// </summary>
    public static void Show(Control owner, GlobalSettings settings, Point? location, Action pasteClipboard, Action<string> pasteText)
    {
        if (owner == null || owner.IsDisposed)
        {
            return;
        }
        PasteSettings options = PasteGuard.OptionsOf(settings);
        ContextMenuStrip menu = new ContextMenuStrip { ShowItemToolTips = true };
        ContextMenuStrip reference = owner.ContextMenuStrip;
        if (reference != null)
        {
            menu.Renderer = reference.Renderer;
            menu.BackColor = reference.BackColor;
            menu.ForeColor = reference.ForeColor;
        }
        menu.Closed += (sender, e) =>
        {
            // Disposing inside the click handler would take the item away while it runs.
            owner.BeginInvoke((Action)menu.Dispose);
        };

        AddClipboardItem(menu, owner, pasteClipboard);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Header("Recent pastes"));
        string[] history = PasteHistory.Snapshot(options.EffectiveHistorySize);
        if (history.Length == 0)
        {
            menu.Items.Add(Disabled(options.KeepHistory ? "(nothing pasted yet)" : "(the history is turned off in Settings)"));
        }
        for (int i = 0; i < history.Length; i++)
        {
            menu.Items.Add(TextItem(i, history[i], pasteText));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Header("Snippets"));
        List<PasteSnippet> snippets = options.Snippets ?? new List<PasteSnippet>();
        if (snippets.Count == 0)
        {
            menu.Items.Add(Disabled("(no snippets saved)"));
        }
        foreach (PasteSnippet snippet in snippets)
        {
            if (snippet == null || string.IsNullOrEmpty(snippet.Text))
            {
                continue;
            }
            ToolStripMenuItem item = new ToolStripMenuItem(Escape(string.IsNullOrWhiteSpace(snippet.Name) ? PasteHistory.Preview(snippet.Text, 30) : snippet.Name))
            {
                ToolTipText = Cut(snippet.Text)
            };
            string text = snippet.Text;
            item.Click += (sender, e) => pasteText(text);
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem add = new ToolStripMenuItem("Save the clipboard as a snippet...");
        add.Click += (sender, e) => AddSnippet(owner, settings, options);
        menu.Items.Add(add);
        if (snippets.Count > 0)
        {
            ToolStripMenuItem edit = new ToolStripMenuItem("Edit or delete a snippet");
            foreach (PasteSnippet snippet in snippets)
            {
                if (snippet == null)
                {
                    continue;
                }
                PasteSnippet target = snippet;
                ToolStripMenuItem editItem = new ToolStripMenuItem(Escape(string.IsNullOrWhiteSpace(snippet.Name) ? PasteHistory.Preview(snippet.Text, 30) : snippet.Name));
                editItem.Click += (sender, e) => EditSnippet(owner, settings, options, target);
                edit.DropDownItems.Add(editItem);
            }
            menu.Items.Add(edit);
        }
        ToolStripMenuItem clear = new ToolStripMenuItem("Clear the history") { Enabled = history.Length > 0 };
        clear.Click += (sender, e) => PasteHistory.Clear();
        menu.Items.Add(clear);

        Point where = location ?? DefaultLocation(owner);
        menu.Show(owner, where);
    }

    private static void AddClipboardItem(ContextMenuStrip menu, Control owner, Action pasteClipboard)
    {
        string label;
        bool enabled = true;
        string text = null;
        try
        {
            text = Win32Clipboard.GetText(owner.Handle);
        }
        catch
        {
        }
        if (!string.IsNullOrEmpty(text))
        {
            label = "Paste the clipboard: " + Escape(PasteHistory.Preview(text, PreviewLength));
        }
        else if (Win32Clipboard.HasFiles())
        {
            label = "Paste the clipboard (files)";
        }
        else
        {
            label = "Paste the clipboard (it is empty)";
            enabled = false;
        }
        ToolStripMenuItem item = new ToolStripMenuItem(label) { Enabled = enabled, ToolTipText = Cut(text) };
        item.Click += (sender, e) => pasteClipboard();
        menu.Items.Add(item);
    }

    private static ToolStripMenuItem TextItem(int index, string text, Action<string> pasteText)
    {
        string preview = Escape(PasteHistory.Preview(text, PreviewLength));
        ToolStripMenuItem item = new ToolStripMenuItem(index < 9 ? "&" + (index + 1) + "  " + preview : preview) { ToolTipText = Cut(text) };
        item.Click += (sender, e) => pasteText(text);
        return item;
    }

    private static void AddSnippet(Control owner, GlobalSettings settings, PasteSettings options)
    {
        string text = null;
        try
        {
            text = Win32Clipboard.GetText(owner.Handle);
        }
        catch
        {
        }
        using SnippetEditForm form = new SnippetEditForm("", text ?? "", canDelete: false);
        if (form.ShowDialog(owner) != DialogResult.OK)
        {
            return;
        }
        options.Snippets ??= new List<PasteSnippet>();
        options.Snippets.Add(new PasteSnippet { Name = form.SnippetName, Text = form.SnippetText });
        settings?.Save();
    }

    private static void EditSnippet(Control owner, GlobalSettings settings, PasteSettings options, PasteSnippet snippet)
    {
        using SnippetEditForm form = new SnippetEditForm(snippet.Name, snippet.Text, canDelete: true);
        DialogResult result = form.ShowDialog(owner);
        if (result == DialogResult.OK)
        {
            snippet.Name = form.SnippetName;
            snippet.Text = form.SnippetText;
        }
        else if (result == DialogResult.Abort)
        {
            options.Snippets.Remove(snippet);
        }
        else
        {
            return;
        }
        settings?.Save();
    }

    private static Point DefaultLocation(Control owner)
    {
        Point mouse = owner.PointToClient(Cursor.Position);
        return owner.ClientRectangle.Contains(mouse) ? mouse : new Point(12, 12);
    }

    private static ToolStripMenuItem Header(string text)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text) { Enabled = false };
        item.Font = new Font(item.Font, FontStyle.Bold);
        return item;
    }

    private static ToolStripMenuItem Disabled(string text)
    {
        return new ToolStripMenuItem(text) { Enabled = false };
    }

    /// <summary>"&amp;" in a menu item text makes the next letter a hotkey: pasted text must show as it is.</summary>
    private static string Escape(string text)
    {
        return (text ?? "").Replace("&", "&&");
    }

    private static string Cut(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        return text.Length > ToolTipLength ? text.Substring(0, ToolTipLength) + "..." : text;
    }
}
