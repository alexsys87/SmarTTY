using System;
using SmarTTY.Controls;

// Regression harness for the rules that enable the buttons and menu items of the file editor tool bar (Save, Undo, Redo,
// Cut, Copy, Paste, Delete, Select All, Find, Replace, Go to Line).
// Run with: dotnet run --project tests/EditorCommandStateTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static int Main()
    {
        // A file that is still being downloaded: only the commands that do not change the text work.
        EditorCommandState loading = new EditorCommandState { ReadOnly = true, HasText = true, HasSelection = true, IsModified = true, CanUndo = true, CanRedo = true };
        Check(!loading.IsEnabled(EditorToolbarCommand.Save), "loading: Save is off");
        Check(!loading.IsEnabled(EditorToolbarCommand.Undo), "loading: Undo is off");
        Check(!loading.IsEnabled(EditorToolbarCommand.Redo), "loading: Redo is off");
        Check(!loading.IsEnabled(EditorToolbarCommand.Cut), "loading: Cut is off");
        Check(!loading.IsEnabled(EditorToolbarCommand.Paste), "loading: Paste is off");
        Check(!loading.IsEnabled(EditorToolbarCommand.Delete), "loading: Delete is off");
        Check(!loading.IsEnabled(EditorToolbarCommand.Replace), "loading: Replace is off");
        Check(loading.IsEnabled(EditorToolbarCommand.Copy), "loading: Copy works");
        Check(loading.IsEnabled(EditorToolbarCommand.SelectAll), "loading: Select All works");
        Check(loading.IsEnabled(EditorToolbarCommand.Find), "loading: Find works");
        Check(loading.IsEnabled(EditorToolbarCommand.GoToLine), "loading: Go to Line works");
        Check(!loading.IsEnabled(EditorToolbarCommand.ToggleHex), "loading: Hex Editor is off");

        // An opened file nobody touched.
        EditorCommandState clean = new EditorCommandState { HasText = true };
        Check(!clean.IsEnabled(EditorToolbarCommand.Save), "unmodified file: Save is off");
        Check(!clean.IsEnabled(EditorToolbarCommand.Undo), "no history: Undo is off");
        Check(!clean.IsEnabled(EditorToolbarCommand.Redo), "no undone edits: Redo is off");
        Check(!clean.IsEnabled(EditorToolbarCommand.Delete), "no selection: Delete is off");
        Check(clean.IsEnabled(EditorToolbarCommand.Paste), "editable: Paste is on");
        Check(clean.IsEnabled(EditorToolbarCommand.Replace), "editable: Replace is on");

        // Without a selection Cut and Copy take the caret line, so they need some text.
        Check(clean.IsEnabled(EditorToolbarCommand.Cut), "text, no selection: Cut takes the line");
        Check(clean.IsEnabled(EditorToolbarCommand.Copy), "text, no selection: Copy takes the line");
        EditorCommandState empty = new EditorCommandState();
        Check(!empty.IsEnabled(EditorToolbarCommand.Cut), "empty document: Cut is off");
        Check(!empty.IsEnabled(EditorToolbarCommand.Copy), "empty document: Copy is off");
        Check(!empty.IsEnabled(EditorToolbarCommand.SelectAll), "empty document: Select All is off");
        Check(empty.IsEnabled(EditorToolbarCommand.Find), "empty document: Find is on");
        Check(empty.IsEnabled(EditorToolbarCommand.GoToLine), "empty document: Go to Line is on");
        Check(empty.IsEnabled(EditorToolbarCommand.ShowAllCharacters), "empty document: Show All Characters is on");

        // Show All Characters is a switch: its tick follows the editor, not the other commands.
        Check(!empty.IsChecked(EditorToolbarCommand.ShowAllCharacters), "Show All Characters starts off");
        Check(new EditorCommandState { ShowAllCharacters = true }.IsChecked(EditorToolbarCommand.ShowAllCharacters),
            "Show All Characters is ticked when the editor shows the marks");
        Check(!new EditorCommandState { ShowAllCharacters = true }.IsChecked(EditorToolbarCommand.Save),
            "only Show All Characters has a tick");

        // Word Wrap is a switch as well, and has no meaning in the hex view.
        Check(empty.IsEnabled(EditorToolbarCommand.WordWrap), "empty document: Word Wrap is on");
        Check(!empty.IsChecked(EditorToolbarCommand.WordWrap), "Word Wrap starts off");
        Check(new EditorCommandState { WordWrap = true }.IsChecked(EditorToolbarCommand.WordWrap), "Word Wrap is ticked when the editor wraps lines");
        Check(!new EditorCommandState { HexMode = true }.IsEnabled(EditorToolbarCommand.WordWrap), "hex view: Word Wrap is off");

        // Edits.
        EditorCommandState edited = new EditorCommandState { HasText = true, IsModified = true, CanUndo = true };
        Check(edited.IsEnabled(EditorToolbarCommand.Save), "modified file: Save is on");
        Check(edited.IsEnabled(EditorToolbarCommand.Undo), "history: Undo is on");
        Check(!edited.IsEnabled(EditorToolbarCommand.Redo), "history only: Redo is off");
        EditorCommandState undone = new EditorCommandState { HasText = true, IsModified = true, CanRedo = true };
        Check(undone.IsEnabled(EditorToolbarCommand.Redo), "undone edit: Redo is on");
        Check(!undone.IsEnabled(EditorToolbarCommand.Undo), "undone everything: Undo is off");

        // A selection.
        EditorCommandState selected = new EditorCommandState { HasText = true, HasSelection = true };
        Check(selected.IsEnabled(EditorToolbarCommand.Delete), "selection: Delete is on");
        Check(selected.IsEnabled(EditorToolbarCommand.Cut), "selection: Cut is on");
        Check(selected.IsEnabled(EditorToolbarCommand.Copy), "selection: Copy is on");

        // A file that does not exist yet is created by saving, even when it is still empty.
        EditorCommandState newFile = new EditorCommandState { IsNewFile = true };
        Check(newFile.IsEnabled(EditorToolbarCommand.Save), "new empty file: Save creates it");
        Check(!new EditorCommandState { IsNewFile = true, ReadOnly = true }.IsEnabled(EditorToolbarCommand.Save), "new file still loading: Save is off");

        // Reload needs a file on the server and no save in progress.
        Check(clean.IsEnabled(EditorToolbarCommand.Reload), "opened file: Reload is on");
        Check(!new EditorCommandState { IsNewFile = true }.IsEnabled(EditorToolbarCommand.Reload), "new file: nothing to reload");
        Check(!new EditorCommandState { IsSaving = true }.IsEnabled(EditorToolbarCommand.Reload), "saving: Reload is off");
        Check(!loading.IsEnabled(EditorToolbarCommand.Reload), "loading: Reload is off");
        Check(new EditorCommandState { IsModified = true }.IsEnabled(EditorToolbarCommand.Reload), "modified file: Reload is on (the window asks before it drops the changes)");

        // Show Changes needs something that differs from the server, and the text view.
        Check(!clean.IsEnabled(EditorToolbarCommand.ShowChanges), "unmodified file: nothing to show");
        Check(new EditorCommandState { IsModified = true }.IsEnabled(EditorToolbarCommand.ShowChanges), "modified file: Show Changes is on");
        Check(new EditorCommandState { IsNewFile = true }.IsEnabled(EditorToolbarCommand.ShowChanges), "new file: Show Changes is on (against nothing)");
        Check(!new EditorCommandState { IsModified = true, HexMode = true }.IsEnabled(EditorToolbarCommand.ShowChanges), "hex view: Show Changes is off");

        // Run in Terminal needs a file that says how to run it, the text view, and no save in progress.
        Check(!clean.IsEnabled(EditorToolbarCommand.RunFile), "plain file: Run in Terminal is off");
        Check(new EditorCommandState { CanRun = true }.IsEnabled(EditorToolbarCommand.RunFile), "script: Run in Terminal is on");
        Check(new EditorCommandState { CanRun = true, IsModified = true }.IsEnabled(EditorToolbarCommand.RunFile), "modified script: Run in Terminal is on (it asks to save first)");
        Check(new EditorCommandState { CanRun = true, IsNewFile = true }.IsEnabled(EditorToolbarCommand.RunFile), "new script: Run in Terminal is on (it asks to save first)");
        Check(new EditorCommandState { CanRun = true, ReadOnly = true }.IsEnabled(EditorToolbarCommand.RunFile), "script that cannot be written: it can still be run");
        Check(!new EditorCommandState { CanRun = true, IsSaving = true }.IsEnabled(EditorToolbarCommand.RunFile), "saving: Run in Terminal is off");
        Check(!new EditorCommandState { CanRun = true, HexMode = true }.IsEnabled(EditorToolbarCommand.RunFile), "hex view: Run in Terminal is off");
        Check(EditorCommandCatalog.Describe(EditorToolbarCommand.RunFile).Shortcut == "F5", "Run in Terminal is on F5");

        // Follow Log follows the file on the server, so a modified or read-only editor does not matter; the hex view does.
        Check(!clean.IsEnabled(EditorToolbarCommand.FollowLog), "file not loaded or new: Follow Log is off");
        Check(new EditorCommandState { CanFollow = true }.IsEnabled(EditorToolbarCommand.FollowLog), "opened file: Follow Log is on");
        Check(new EditorCommandState { CanFollow = true, IsModified = true, ReadOnly = true, IsSaving = true }.IsEnabled(EditorToolbarCommand.FollowLog), "modified, read-only or saving: Follow Log is still on");
        Check(!new EditorCommandState { CanFollow = true, HexMode = true }.IsEnabled(EditorToolbarCommand.FollowLog), "hex view: Follow Log is off");

        // The hex view: it can be opened once the file is loaded, and Replace belongs to the text view.
        Check(new EditorCommandState { CanUseHex = true }.IsEnabled(EditorToolbarCommand.ToggleHex), "loaded file: Hex Editor is on");
        Check(!new EditorCommandState { CanUseHex = false }.IsEnabled(EditorToolbarCommand.ToggleHex), "not loaded: Hex Editor is off");
        Check(!new EditorCommandState { CanUseHex = true, IsSaving = true }.IsEnabled(EditorToolbarCommand.ToggleHex), "saving: Hex Editor is off");
        EditorCommandState hex = new EditorCommandState { CanUseHex = true, HexMode = true, HasText = true };
        Check(hex.IsEnabled(EditorToolbarCommand.ToggleHex), "hex view: Hex Editor can be closed");
        Check(!hex.IsEnabled(EditorToolbarCommand.Replace), "hex view: Replace is off");
        Check(hex.IsEnabled(EditorToolbarCommand.Find), "hex view: Find is on");

        // The tool bar and the context menu list every command once, and each has a label and an icon.
        foreach (EditorToolbarCommand command in Enum.GetValues(typeof(EditorToolbarCommand)))
        {
            Check(Array.IndexOf(EditorCommandCatalog.ToolBarLayout, (EditorToolbarCommand?)command) >= 0, "tool bar: " + command + " is listed");
            Check(Array.IndexOf(EditorCommandCatalog.ContextMenuLayout, (EditorToolbarCommand?)command) >= 0, "context menu: " + command + " is listed");
            EditorCommandDescription description = EditorCommandCatalog.Describe(command);
            Check(description.Text.Length > 0 && description.IconName.Length > 0 && description.IconPath.Length > 0, "catalog: " + command + " has a label and icons");
        }
        Check(EditorCommandCatalog.Describe(EditorToolbarCommand.Reload).ToolTip == "Reload from Server", "catalog: a command without a key has a plain tool tip");
        Check(EditorCommandCatalog.Describe(EditorToolbarCommand.Save).ToolTip == "Save (Ctrl+S)", "catalog: a command with a key shows it in the tool tip");

        // The status bar texts.
        Check(EditorStatusText.Position(0, 0) == "Ln 1, Col 1", "status: the first position is Ln 1, Col 1");
        Check(EditorStatusText.Position(11, 4) == "Ln 12, Col 5", "status: positions are counted from 1");
        Check(EditorStatusText.Selection(1, 1) == "1 char", "status: one character");
        Check(EditorStatusText.Selection(25, 1) == "25 chars", "status: a selection in one line");
        Check(EditorStatusText.Selection(40, 3) == "40 chars, 3 lines", "status: a selection over several lines");
        Check(EditorStatusText.HexPosition(0, 16, insertMode: false) == "Offset 0x00000000 of 0x00000010 (OVR)", "status: the hex offset and size");
        Check(EditorStatusText.HexPosition(0x1F, 0x100, insertMode: true) == "Offset 0x0000001F of 0x00000100 (INS)", "status: the hex insert mode");
        Check(EditorStatusText.HexSelection(1) == "1 byte", "status: one selected byte");
        Check(EditorStatusText.HexSelection(256) == "256 bytes", "status: selected bytes");
        Check(EditorStatusText.State(true, false, false, true, false) == "Loading...", "status: loading wins");
        Check(EditorStatusText.State(false, false, true, true, false) == "Saving...", "status: saving before modified");
        Check(EditorStatusText.State(false, false, false, true, false) == "Modified", "status: modified");
        Check(EditorStatusText.State(false, true, false, true, false) == "Read-only", "status: a file opened read-only says so");
        Check(EditorStatusText.State(true, true, false, false, false) == "Loading...", "status: loading wins over read-only");
        Check(EditorStatusText.State(false, false, false, true, true) == "New file, modified", "status: a new file that was typed in");
        Check(EditorStatusText.State(false, false, false, false, true) == "New file", "status: a new empty file");
        Check(EditorStatusText.State(false, false, false, false, false) == "Saved", "status: nothing to save");

        // The Text menu: it needs text that can be changed; sorting and removing duplicates need lines selected.
        foreach (SmarTTY.CodeEditing.LineTransformKind kind in Enum.GetValues(typeof(SmarTTY.CodeEditing.LineTransformKind)))
        {
            bool needsLines = SmarTTY.CodeEditing.LineTransforms.NeedsSelection(kind);
            Check(new EditorCommandState { HasLineSelection = true }.IsEnabled(kind), "text menu: " + kind + " is on for an editable text with lines selected");
            Check(new EditorCommandState().IsEnabled(kind) == !needsLines, "text menu: " + kind + (needsLines ? " needs lines selected" : " works without a selection"));
            Check(!new EditorCommandState { ReadOnly = true, HasLineSelection = true }.IsEnabled(kind), "text menu: " + kind + " is off in a read-only editor");
            Check(!new EditorCommandState { HexMode = true, HasLineSelection = true }.IsEnabled(kind), "text menu: " + kind + " is off in the hex view");
            Check(SmarTTY.CodeEditing.LineTransforms.Title(kind).Length > 0, "text menu: " + kind + " has a title");
        }

        // Every command has a rule (an unknown value is off).
        foreach (EditorToolbarCommand command in Enum.GetValues(typeof(EditorToolbarCommand)))
        {
            bool everythingOn = new EditorCommandState { HasText = true, HasSelection = true, CanUndo = true, CanRedo = true, IsModified = true, CanUseHex = true, CanRun = true, CanFollow = true }.IsEnabled(command);
            Check(everythingOn, "all conditions met: " + command + " is on");
        }
        Check(!empty.IsEnabled((EditorToolbarCommand)999), "unknown command is off");

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
