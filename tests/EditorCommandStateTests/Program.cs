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

        // Every command has a rule (an unknown value is off).
        foreach (EditorToolbarCommand command in Enum.GetValues(typeof(EditorToolbarCommand)))
        {
            bool everythingOn = new EditorCommandState { HasText = true, HasSelection = true, CanUndo = true, CanRedo = true, IsModified = true }.IsEnabled(command);
            Check(everythingOn, "all conditions met: " + command + " is on");
        }
        Check(!empty.IsEnabled((EditorToolbarCommand)999), "unknown command is off");

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
