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
