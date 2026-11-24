using SmarTTY.Controls;
using SmarTTY.Native;
using SmarTTY.Terminal;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// The paste menu of a terminal: the clipboard, the texts pasted lately (kept in memory only) and the snippets the user saved. Every item
/// is pasted through the terminal's own paste, so cleaning and confirmations apply as to any paste. WPF since stage 4A (it was a WinForms
/// <c>ContextMenuStrip</c> in <c>Controls/PasteMenu</c>); the texts, the order and the tool tips are those of the WinForms menu.
/// </summary>
internal static class PasteMenu
{
    private const int PreviewLength = 60;

    private const int ToolTipLength = 600;

    /// <summary>
    /// Opens the menu over <paramref name="target"/> (a terminal): at the mouse when it is over the terminal, otherwise near its top left
    /// corner (Ctrl+Shift+Insert with the mouse elsewhere).
    /// </summary>
    public static void Show(FrameworkElement target, GlobalSettings settings, Action pasteClipboard, Action<string> pasteText)
    {
        if (target == null || PresentationSource.FromVisual(target) == null)
        {
            return;
        }
        PasteSettings options = PasteGuard.OptionsOf(settings);
        ContextMenu menu = new ContextMenu();
        IntPtr handle = (PresentationSource.FromVisual(target) as HwndSource)?.Handle ?? IntPtr.Zero;
        Window owner = Window.GetWindow(target);

        AddClipboardItem(menu, handle, pasteClipboard);

        menu.Items.Add(new Separator());
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

        menu.Items.Add(new Separator());
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
            string text = snippet.Text;
            menu.Items.Add(WpfMenus.Item(WpfMenus.PlainText(SnippetName(snippet)), null, () => pasteText(text), Cut(snippet.Text)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(WpfMenus.Item("Save the clipboard as a snippet...", null, () => AddSnippet(owner, handle, settings, options)));
        if (snippets.Count > 0)
        {
            MenuItem edit = WpfMenus.Item("Edit or delete a snippet", null, null);
            foreach (PasteSnippet snippet in snippets)
            {
                if (snippet == null)
                {
                    continue;
                }
                PasteSnippet chosen = snippet;
                edit.Items.Add(WpfMenus.Item(WpfMenus.PlainText(SnippetName(snippet)), null, () => EditSnippet(owner, settings, options, chosen)));
            }
            menu.Items.Add(edit);
        }
        MenuItem clear = WpfMenus.Item("Clear the history", null, PasteHistory.Clear);
        clear.IsEnabled = history.Length > 0;
        menu.Items.Add(clear);

        menu.PlacementTarget = target;
        if (target.IsMouseOver)
        {
            menu.Placement = PlacementMode.MousePoint;
        }
        else
        {
            menu.Placement = PlacementMode.Relative;
            menu.HorizontalOffset = 12;
            menu.VerticalOffset = 12;
        }
        menu.IsOpen = true;
    }

    private static string SnippetName(PasteSnippet snippet)
    {
        return string.IsNullOrWhiteSpace(snippet.Name) ? PasteHistory.Preview(snippet.Text, 30) : snippet.Name;
    }

    private static void AddClipboardItem(ContextMenu menu, IntPtr handle, Action pasteClipboard)
    {
        string label;
        bool enabled = true;
        string text = null;
        try
        {
            text = Win32Clipboard.GetText(handle);
        }
        catch
        {
        }
        if (!string.IsNullOrEmpty(text))
        {
            label = "Paste the clipboard: " + WpfMenus.PlainText(PasteHistory.Preview(text, PreviewLength));
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
        MenuItem item = WpfMenus.Item(label, null, pasteClipboard, Cut(text));
        item.IsEnabled = enabled;
        menu.Items.Add(item);
    }

    // The first nine have their number as the access key, as in the WinForms menu ("&1").
    private static MenuItem TextItem(int index, string text, Action<string> pasteText)
    {
        string preview = WpfMenus.PlainText(PasteHistory.Preview(text, PreviewLength));
        return WpfMenus.Item(index < 9 ? "_" + (index + 1) + "  " + preview : preview, null, () => pasteText(text), Cut(text));
    }

    private static void AddSnippet(Window owner, IntPtr handle, GlobalSettings settings, PasteSettings options)
    {
        string text = null;
        try
        {
            text = Win32Clipboard.GetText(handle);
        }
        catch
        {
        }
        SnippetEditResult added = SnippetEditWindow.Edit(owner, "", text ?? "", canDelete: false);
        if (added == null)
        {
            return;
        }
        options.Snippets ??= new List<PasteSnippet>();
        options.Snippets.Add(new PasteSnippet { Name = added.Name, Text = added.Text });
        settings?.Save();
    }

    private static void EditSnippet(Window owner, GlobalSettings settings, PasteSettings options, PasteSnippet snippet)
    {
        SnippetEditResult edited = SnippetEditWindow.Edit(owner, snippet.Name, snippet.Text, canDelete: true);
        if (edited == null)
        {
            return;
        }
        if (edited.Delete)
        {
            options.Snippets.Remove(snippet);
        }
        else
        {
            snippet.Name = edited.Name;
            snippet.Text = edited.Text;
        }
        settings?.Save();
    }

    private static MenuItem Header(string text)
    {
        MenuItem item = Disabled(text);
        item.FontWeight = FontWeights.Bold;
        return item;
    }

    private static MenuItem Disabled(string text)
    {
        return new MenuItem { Header = WpfMenus.PlainText(text), IsEnabled = false };
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
