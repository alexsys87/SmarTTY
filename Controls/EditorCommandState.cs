using System;

namespace SmarTTY.Controls;

/// <summary>The commands of the file editor tool bar and context menu.</summary>
internal enum EditorToolbarCommand
{
    Save,
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    Delete,
    SelectAll,
    Find,
    Replace,
    GoToLine
}

/// <summary>
/// What the editor allows right now, and from it which commands are enabled. Kept apart from the WinForms controls so
/// that the rules can be tested without a window.
/// </summary>
internal struct EditorCommandState
{
    /// <summary>The text cannot be changed (the file is still being downloaded).</summary>
    public bool ReadOnly { get; set; }

    public bool HasSelection { get; set; }

    /// <summary>The document holds at least one character.</summary>
    public bool HasText { get; set; }

    public bool CanUndo { get; set; }

    public bool CanRedo { get; set; }

    /// <summary>There are changes that were not saved yet.</summary>
    public bool IsModified { get; set; }

    /// <summary>The file does not exist yet: saving creates it, even when nothing was typed.</summary>
    public bool IsNewFile { get; set; }

    public bool IsEnabled(EditorToolbarCommand command)
    {
        switch (command)
        {
            case EditorToolbarCommand.Save:
                return !ReadOnly && (IsModified || IsNewFile);
            case EditorToolbarCommand.Undo:
                return !ReadOnly && CanUndo;
            case EditorToolbarCommand.Redo:
                return !ReadOnly && CanRedo;
            case EditorToolbarCommand.Cut:
                // Without a selection Cut and Copy take the line of the caret, as Ctrl+X and Ctrl+C do.
                return !ReadOnly && (HasSelection || HasText);
            case EditorToolbarCommand.Copy:
                return HasSelection || HasText;
            case EditorToolbarCommand.Paste:
                return !ReadOnly;
            case EditorToolbarCommand.Delete:
                return !ReadOnly && HasSelection;
            case EditorToolbarCommand.SelectAll:
                return HasText;
            case EditorToolbarCommand.Replace:
                return !ReadOnly;
            case EditorToolbarCommand.Find:
            case EditorToolbarCommand.GoToLine:
                return true;
            default:
                return false;
        }
    }
}

/// <summary>The label, icon and shortcut of an editor command, shared by the WPF tool bar and the WinForms menu.</summary>
internal readonly struct EditorCommandDescription
{
    public EditorCommandDescription(string text, string iconName, string iconPath, string shortcut)
    {
        Text = text;
        IconName = iconName;
        IconPath = iconPath;
        Shortcut = shortcut;
    }

    public string Text { get; }

    /// <summary>The embedded image name that Icons loads for the WinForms menu.</summary>
    public string IconName { get; }

    /// <summary>The path under icons/ that the WPF tool bar loads as a resource.</summary>
    public string IconPath { get; }

    /// <summary>Shown next to the menu item; the editor itself handles the key.</summary>
    public string Shortcut { get; }

    public string ToolTip => Text.TrimEnd('.') + " (" + Shortcut + ")";
}

/// <summary>
/// The commands in the order of the WPF tool bar and of the WinForms context menu. Kept apart from both so that the
/// two stay in step and the rules can be tested without a window.
/// </summary>
internal static class EditorCommandCatalog
{
    // null is a separator.
    public static readonly EditorToolbarCommand?[] ToolBarLayout =
    {
        EditorToolbarCommand.Save,
        null,
        EditorToolbarCommand.Undo,
        EditorToolbarCommand.Redo,
        null,
        EditorToolbarCommand.Cut,
        EditorToolbarCommand.Copy,
        EditorToolbarCommand.Paste,
        EditorToolbarCommand.Delete,
        null,
        EditorToolbarCommand.SelectAll,
        null,
        EditorToolbarCommand.Find,
        EditorToolbarCommand.Replace,
        EditorToolbarCommand.GoToLine
    };

    public static readonly EditorToolbarCommand?[] ContextMenuLayout =
    {
        EditorToolbarCommand.Undo,
        EditorToolbarCommand.Redo,
        null,
        EditorToolbarCommand.Cut,
        EditorToolbarCommand.Copy,
        EditorToolbarCommand.Paste,
        EditorToolbarCommand.Delete,
        null,
        EditorToolbarCommand.SelectAll,
        null,
        EditorToolbarCommand.Find,
        EditorToolbarCommand.Replace,
        EditorToolbarCommand.GoToLine,
        null,
        EditorToolbarCommand.Save
    };

    public static EditorCommandDescription Describe(EditorToolbarCommand command)
    {
        switch (command)
        {
            case EditorToolbarCommand.Save:
                return new EditorCommandDescription("Save", "save", "MainWindow/save16.png", "Ctrl+S");
            case EditorToolbarCommand.Undo:
                return new EditorCommandDescription("Undo", "undo", "editor/undo.png", "Ctrl+Z");
            case EditorToolbarCommand.Redo:
                return new EditorCommandDescription("Redo", "redo", "editor/redo.png", "Ctrl+Y");
            case EditorToolbarCommand.Cut:
                return new EditorCommandDescription("Cut", "cut", "menu/cut.png", "Ctrl+X");
            case EditorToolbarCommand.Copy:
                return new EditorCommandDescription("Copy", "copy", "MainWindow/copy.png", "Ctrl+C");
            case EditorToolbarCommand.Paste:
                return new EditorCommandDescription("Paste", "paste", "MainWindow/paste.png", "Ctrl+V");
            case EditorToolbarCommand.Delete:
                return new EditorCommandDescription("Delete", "delete", "delete16.png", "Del");
            case EditorToolbarCommand.SelectAll:
                return new EditorCommandDescription("Select All", "select-all", "editor/select-all.png", "Ctrl+A");
            case EditorToolbarCommand.Find:
                return new EditorCommandDescription("Find", "find", "MainWindow/FindHS.png", "Ctrl+F");
            case EditorToolbarCommand.Replace:
                return new EditorCommandDescription("Replace", "replace", "editor/replace.png", "Ctrl+H");
            case EditorToolbarCommand.GoToLine:
                return new EditorCommandDescription("Go to Line...", "goto-line", "editor/goto-line.png", "Ctrl+G");
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}
