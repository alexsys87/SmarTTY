using SmarTTY.GUI.Styles;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// A remote file editor with a tool bar above it and a context menu in it. Both offer the same commands: Save, Undo, Redo,
/// Cut, Copy, Paste, Delete, Select All, Find, Replace and Go to Line. The commands that need the main window (saving,
/// the search bar, the Go to Line dialog) are raised as events; the others act on the editor directly.
/// </summary>
internal sealed class RemoteFileEditorPanel : UserControl
{
    private sealed class CommandItems
    {
        public ToolStripButton Button;

        public ToolStripMenuItem MenuItem;
    }

    // null is a separator.
    private static readonly EditorToolbarCommand?[] ToolbarLayout =
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

    private static readonly EditorToolbarCommand?[] MenuLayout =
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

    private readonly RemoteFileEditor _editor;

    private readonly ToolStrip _toolStrip;

    private readonly ContextMenuStrip _contextMenu;

    private readonly Dictionary<EditorToolbarCommand, CommandItems> _commands = new Dictionary<EditorToolbarCommand, CommandItems>();

    private ToolbarPalette _palette;

    private ToolStripRenderer _renderer;

    private bool _refreshPending;

    /// <summary>The Save button or menu item was used.</summary>
    public event EventHandler SaveRequested;

    public event EventHandler FindRequested;

    public event EventHandler ReplaceRequested;

    public event EventHandler GoToLineRequested;

    public RemoteFileEditorPanel(RemoteFileEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        SuspendLayout();
        CreateCommand(EditorToolbarCommand.Save, "Save", "save", "Ctrl+S");
        CreateCommand(EditorToolbarCommand.Undo, "Undo", "undo", "Ctrl+Z");
        CreateCommand(EditorToolbarCommand.Redo, "Redo", "redo", "Ctrl+Y");
        CreateCommand(EditorToolbarCommand.Cut, "Cut", "cut", "Ctrl+X");
        CreateCommand(EditorToolbarCommand.Copy, "Copy", "copy", "Ctrl+C");
        CreateCommand(EditorToolbarCommand.Paste, "Paste", "paste", "Ctrl+V");
        CreateCommand(EditorToolbarCommand.Delete, "Delete", "delete", "Del");
        CreateCommand(EditorToolbarCommand.SelectAll, "Select All", "select-all", "Ctrl+A");
        CreateCommand(EditorToolbarCommand.Find, "Find", "find", "Ctrl+F");
        CreateCommand(EditorToolbarCommand.Replace, "Replace", "replace", "Ctrl+H");
        CreateCommand(EditorToolbarCommand.GoToLine, "Go to Line...", "goto-line", "Ctrl+G");

        _toolStrip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            CanOverflow = true,
            ShowItemToolTips = true,
            TabStop = false
        };
        foreach (EditorToolbarCommand? command in ToolbarLayout)
        {
            _toolStrip.Items.Add(command.HasValue ? (ToolStripItem)_commands[command.Value].Button : new ToolStripSeparator());
        }

        _contextMenu = new ContextMenuStrip();
        foreach (EditorToolbarCommand? command in MenuLayout)
        {
            _contextMenu.Items.Add(command.HasValue ? (ToolStripItem)_commands[command.Value].MenuItem : new ToolStripSeparator());
        }
        _contextMenu.Opening += (sender, e) =>
        {
            ApplyMenuTheme(_contextMenu);
            RefreshState();
        };
        _editor.ContextMenuStrip = _contextMenu;

        // The docked editor fills what the tool bar leaves; it is added first so that the tool bar is laid out first.
        _editor.Dock = DockStyle.Fill;
        Controls.Add(_editor);
        Controls.Add(_toolStrip);
        ActiveControl = _editor;
        ResumeLayout(performLayout: true);

        // What the buttons depend on: the selection, the text and its undo history, the saved state and the load.
        _editor.SelectionChanged += (editorControl, previousCaret, currentCaret, hasSelection) => ScheduleRefresh();
        _editor.ModifiedChanged += (editorControl, modified) => ScheduleRefresh();
        _editor.RangeChanged += (editorControl, start, oldEnd, newEnd) => ScheduleRefresh();
        _editor.LineChanged += (editorControl, line, column, addedLength, removedLength) => ScheduleRefresh();
        _editor.KeyUp += (sender, e) => ScheduleRefresh();
        _editor.SavingChanged += (sender, e) => ScheduleRefresh();
        _editor.LoadCompleted += (sender, e) => ScheduleRefresh();

        ThemeManager.ThemeChanged += OnThemeChanged;
        ApplyToolbarTheme();
        RefreshState();
    }

    private void CreateCommand(EditorToolbarCommand command, string text, string iconName, string shortcut)
    {
        string toolTip = text.TrimEnd('.') + " (" + shortcut + ")";
        ToolStripButton button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            AutoToolTip = false,
            ToolTipText = toolTip
        };
        // The key is shown only: the editor handles the key itself, a menu shortcut would run the command twice.
        ToolStripMenuItem menuItem = new ToolStripMenuItem(text)
        {
            ShortcutKeyDisplayString = shortcut
        };
        Icons.Apply(button, iconName);
        Icons.Apply(menuItem, iconName);
        button.Click += (sender, e) => Run(command);
        menuItem.Click += (sender, e) => Run(command);
        _commands[command] = new CommandItems { Button = button, MenuItem = menuItem };
    }

    private void Run(EditorToolbarCommand command)
    {
        // A command that enabled state is stale could slip through between a change and its refresh.
        if (!CaptureState().IsEnabled(command))
        {
            RefreshState();
            return;
        }
        // The search bar and the Go to Line dialog take the focus themselves.
        bool focusEditor = true;
        try
        {
            switch (command)
            {
                case EditorToolbarCommand.Save:
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case EditorToolbarCommand.Undo:
                    _editor.Undo();
                    break;
                case EditorToolbarCommand.Redo:
                    _editor.Redo();
                    break;
                case EditorToolbarCommand.Cut:
                    _editor.Cut();
                    break;
                case EditorToolbarCommand.Copy:
                    _editor.CopySelection();
                    break;
                case EditorToolbarCommand.Paste:
                    _editor.Paste();
                    break;
                case EditorToolbarCommand.Delete:
                    _editor.DeleteSelection();
                    break;
                case EditorToolbarCommand.SelectAll:
                    _editor.SelectAllText();
                    break;
                case EditorToolbarCommand.Find:
                    focusEditor = false;
                    FindRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case EditorToolbarCommand.Replace:
                    focusEditor = false;
                    ReplaceRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case EditorToolbarCommand.GoToLine:
                    focusEditor = false;
                    GoToLineRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
        if (focusEditor)
        {
            _editor.Focus();
        }
        RefreshState();
    }

    private EditorCommandState CaptureState()
    {
        int lineCount = _editor.LineCount;
        return new EditorCommandState
        {
            ReadOnly = _editor.ReadOnly,
            HasSelection = _editor.HasSelection && _editor.SelectionStart != _editor.SelectionEnd,
            HasText = lineCount > 1 || (lineCount == 1 && _editor.EditableDocument.GetLineText(0).Length > 0),
            CanUndo = _editor.CanUndo,
            CanRedo = _editor.CanRedo,
            IsModified = _editor.IsModified,
            IsNewFile = _editor.IsNewFile
        };
    }

    private void RefreshState()
    {
        EditorCommandState state = CaptureState();
        foreach (KeyValuePair<EditorToolbarCommand, CommandItems> pair in _commands)
        {
            bool enabled = state.IsEnabled(pair.Key);
            pair.Value.Button.Enabled = enabled;
            pair.Value.MenuItem.Enabled = enabled;
        }
    }

    /// <summary>
    /// Updates the buttons after the editor has finished what it is doing: the events arrive in the middle of an edit,
    /// before the undo history is complete, and many of them (a mouse drag) come in a row.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (_refreshPending || !IsHandleCreated || IsDisposed)
        {
            return;
        }
        _refreshPending = true;
        BeginInvoke((MethodInvoker)(() =>
        {
            _refreshPending = false;
            if (!IsDisposed)
            {
                RefreshState();
            }
        }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshState();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        // The host focuses its child: the keys are for the editor, not for the tool bar.
        if (!_editor.Focused)
        {
            _editor.Focus();
        }
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        ApplyToolbarTheme();
    }

    /// <summary>Paints the tool bar and the context menu in the colours of the application theme.</summary>
    private void ApplyToolbarTheme()
    {
        _palette = ToolbarPalette.FromCurrentTheme();
        _renderer = new ThemedToolStripRenderer(_palette);
        _toolStrip.Renderer = _renderer;
        _toolStrip.BackColor = _palette.Background;
        _toolStrip.ForeColor = _palette.Text;
        foreach (ToolStripItem item in _toolStrip.Items)
        {
            item.ForeColor = _palette.Text;
        }
        ApplyMenuTheme(_contextMenu);
        _toolStrip.Invalidate();
    }

    private void ApplyMenuTheme(ToolStripDropDown menu)
    {
        menu.Renderer = _renderer;
        menu.BackColor = _palette.Popup;
        menu.ForeColor = _palette.Text;
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = _palette.Text;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _contextMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
