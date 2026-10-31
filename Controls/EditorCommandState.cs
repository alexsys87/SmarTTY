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
    GoToLine,
    ShowAllCharacters,
    WordWrap,
    Reload,
    ShowChanges,
    RunFile,
    FollowLog,
    ToggleHex
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

    /// <summary>A save is running in the background.</summary>
    public bool IsSaving { get; set; }

    /// <summary>The editor shows spaces, tabs, line ends and other invisible characters as marks.</summary>
    public bool ShowAllCharacters { get; set; }

    /// <summary>The editor continues long lines on the next row instead of scrolling sideways.</summary>
    public bool WordWrap { get; set; }

    /// <summary>The editor shows the bytes in the hex view.</summary>
    public bool HexMode { get; set; }

    /// <summary>The file is on the server and the host has tail: it can be followed in a terminal tab.</summary>
    public bool CanFollow { get; set; }

    /// <summary>The file is loaded and its name or first line says how to run it (shell, Python, Lua, ...).</summary>
    public bool CanRun { get; set; }

    /// <summary>The file is loaded and no save is running: the hex view can be opened.</summary>
    public bool CanUseHex { get; set; }

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
                // The text search and replace window does not know about the bytes in the hex view.
                return !ReadOnly && !HexMode;
            case EditorToolbarCommand.Reload:
                // There is a file on the server to load again, and nothing is being written to it.
                return !ReadOnly && !IsNewFile && !IsSaving;
            case EditorToolbarCommand.WordWrap:
                // The hex view shows rows of bytes.
                return !HexMode;
            case EditorToolbarCommand.FollowLog:
                // It follows the file on the server, whatever the editor holds.
                return CanFollow && !HexMode;
            case EditorToolbarCommand.RunFile:
                // The text view only; unsaved changes are saved first, after a question.
                return CanRun && !HexMode && !IsSaving;
            case EditorToolbarCommand.ShowChanges:
                // There is something to compare with the file on the server (a new file is compared with nothing).
                return !HexMode && (IsModified || IsNewFile);
            case EditorToolbarCommand.Find:
            case EditorToolbarCommand.GoToLine:
            case EditorToolbarCommand.ShowAllCharacters:
                return true;
            case EditorToolbarCommand.ToggleHex:
                return CanUseHex && !IsSaving;
            default:
                return false;
        }
    }

    /// <summary>Whether a command that switches something on is currently on.</summary>
    public bool IsChecked(EditorToolbarCommand command)
    {
        return (command == EditorToolbarCommand.ShowAllCharacters && ShowAllCharacters)
            || (command == EditorToolbarCommand.WordWrap && WordWrap)
            || (command == EditorToolbarCommand.ToggleHex && HexMode);
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

    public string ToolTip => (Shortcut.Length == 0) ? Text : (Text.TrimEnd('.') + " (" + Shortcut + ")");
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
        EditorToolbarCommand.Reload,
        EditorToolbarCommand.ShowChanges,
        EditorToolbarCommand.RunFile,
        EditorToolbarCommand.FollowLog,
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
        EditorToolbarCommand.GoToLine,
        null,
        EditorToolbarCommand.ShowAllCharacters,
        EditorToolbarCommand.WordWrap,
        EditorToolbarCommand.ToggleHex
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
        EditorToolbarCommand.ShowAllCharacters,
        EditorToolbarCommand.WordWrap,
        null,
        EditorToolbarCommand.ToggleHex,
        null,
        EditorToolbarCommand.Save,
        EditorToolbarCommand.Reload,
        EditorToolbarCommand.ShowChanges,
        EditorToolbarCommand.RunFile,
        EditorToolbarCommand.FollowLog
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
            case EditorToolbarCommand.ShowAllCharacters:
                return new EditorCommandDescription("Show All Characters", "show-all-characters", "editor/show-all-characters.png", "");
            case EditorToolbarCommand.WordWrap:
                return new EditorCommandDescription("Word Wrap", "word-wrap", "editor/word-wrap.png", "");
            case EditorToolbarCommand.Reload:
                return new EditorCommandDescription("Reload from Server", "refresh", "MainWindow/refresh16.png", "");
            case EditorToolbarCommand.ShowChanges:
                return new EditorCommandDescription("Show Changes...", "show-changes", "editor/show-changes.png", "");
            case EditorToolbarCommand.RunFile:
                return new EditorCommandDescription("Run in Terminal", "run", "editor/run.png", "F5");
            case EditorToolbarCommand.FollowLog:
                return new EditorCommandDescription("Follow Log (tail -f)...", "follow", "editor/follow.png", "");
            case EditorToolbarCommand.ToggleHex:
                return new EditorCommandDescription("Hex Editor", "hex", "editor/hex.png", "");
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}

/// <summary>The texts of the status bar below the editor.</summary>
internal static class EditorStatusText
{
    /// <summary>The caret: lines and columns are counted from 1, the arguments from 0.</summary>
    public static string Position(int line, int column)
    {
        return "Ln " + (line + 1) + ", Col " + (column + 1);
    }

    /// <summary>The selection: its length in characters (line breaks included), and its lines when it spans several.</summary>
    public static string Selection(int characters, int lines)
    {
        string text = characters + ((characters == 1) ? " char" : " chars");
        return (lines > 1) ? (text + ", " + lines + " lines") : text;
    }

    /// <summary>The caret of the hex view: the byte offset, the size of the file, and the typing mode.</summary>
    public static string HexPosition(int offset, int length, bool insertMode)
    {
        return "Offset 0x" + offset.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
            + " of 0x" + length.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
            + (insertMode ? " (INS)" : " (OVR)");
    }

    /// <summary>The selection of the hex view: its length in bytes.</summary>
    public static string HexSelection(int bytes)
    {
        return bytes + ((bytes == 1) ? " byte" : " bytes");
    }

    /// <summary>Whether the file on the server matches the text in the editor.</summary>
    public static string State(bool loading, bool readOnlyMode, bool saving, bool modified, bool isNewFile)
    {
        if (loading)
        {
            return "Loading...";
        }
        if (readOnlyMode)
        {
            return "Read-only";
        }
        if (saving)
        {
            return "Saving...";
        }
        if (modified)
        {
            return isNewFile ? "New file, modified" : "Modified";
        }
        return isNewFile ? "New file" : "Saved";
    }
}
