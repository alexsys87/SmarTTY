using SmarTTY.Forms;
using SmarTTY.GUI.Styles;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// A remote file editor with its context menu. The WPF tool bar above the editor offers the same commands, and both
/// run them through <see cref="RunCommand"/>. The commands that need the main window (saving, the search bar, the Go
/// to Line dialog) are raised as events; the others act on the editor directly. The WPF status bar below the editor
/// reads the state of the editor when <see cref="StateChanged"/> is raised.
/// </summary>
internal sealed class RemoteFileEditorPanel : UserControl
{
    private readonly RemoteFileEditor _editor;

    private readonly ContextMenuStrip _contextMenu;

    private readonly Dictionary<EditorToolbarCommand, ToolStripMenuItem> _menuItems = new Dictionary<EditorToolbarCommand, ToolStripMenuItem>();

    // The unscaled menu font, and the font for the current monitor (null until the menu is opened).
    private readonly Font _menuFont;

    private Font _scaledMenuFont;

    private ToolbarPalette _palette;

    private ToolStripRenderer _renderer;

    private bool _refreshPending;

    private int _contextMenuDpi;

    /// <summary>The Save button or menu item was used.</summary>
    public event EventHandler SaveRequested;

    public event EventHandler FindRequested;

    public event EventHandler ReplaceRequested;

    public event EventHandler GoToLineRequested;

    /// <summary>The enabled state of the commands changed (for the WPF tool bar).</summary>
    internal event EventHandler StateChanged;

    internal EditorCommandState CommandState { get; private set; }

    public RemoteFileEditorPanel(RemoteFileEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        SuspendLayout();

        _contextMenu = new ContextMenuStrip();
        foreach (EditorToolbarCommand? command in EditorCommandCatalog.ContextMenuLayout)
        {
            _contextMenu.Items.Add(command.HasValue ? (ToolStripItem)CreateMenuItem(command.Value) : new ToolStripSeparator());
        }
        _menuFont = _contextMenu.Font;
        _contextMenu.Opening += (sender, e) =>
        {
            ScaleContextMenu();
            ApplyMenuTheme(_contextMenu);
            RefreshState();
        };
        _editor.ContextMenuStrip = _contextMenu;

        _editor.Dock = DockStyle.Fill;
        Controls.Add(_editor);
        ActiveControl = _editor;
        ResumeLayout(performLayout: true);

        // What the commands depend on: the selection, the text and its undo history, the saved state and the load.
        _editor.SelectionChanged += (editorControl, previousCaret, currentCaret, hasSelection) => ScheduleRefresh();
        _editor.ModifiedChanged += (editorControl, modified) => ScheduleRefresh();
        _editor.RangeChanged += (editorControl, start, oldEnd, newEnd) => ScheduleRefresh();
        _editor.LineChanged += (editorControl, line, column, addedLength, removedLength) => ScheduleRefresh();
        _editor.KeyUp += (sender, e) => ScheduleRefresh();
        _editor.SavingChanged += (sender, e) => ScheduleRefresh();
        _editor.LoadCompleted += (sender, e) => ScheduleRefresh();
        _editor.FormatChanged += (sender, e) => ScheduleRefresh();

        ThemeManager.ThemeChanged += OnThemeChanged;
        ApplyTheme();
        RefreshState();
    }

    private ToolStripMenuItem CreateMenuItem(EditorToolbarCommand command)
    {
        EditorCommandDescription description = EditorCommandCatalog.Describe(command);
        // The key is shown only: the editor handles the key itself, a menu shortcut would run the command twice.
        ToolStripMenuItem menuItem = new ToolStripMenuItem(description.Text)
        {
            ShortcutKeyDisplayString = description.Shortcut
        };
        Icons.Apply(menuItem, description.IconName);
        menuItem.Click += (sender, e) => RunCommand(command);
        _menuItems[command] = menuItem;
        return menuItem;
    }

    /// <summary>Runs a command from the tool bar or the context menu.</summary>
    internal void RunCommand(EditorToolbarCommand command)
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
                case EditorToolbarCommand.Reload:
                    if (ConfirmDiscardChanges("Drop your changes and load " + _editor.RemotePath + " from the server again?"))
                    {
                        _editor.Reload();
                    }
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

    internal EditorCommandState CaptureState()
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
            IsNewFile = _editor.IsNewFile,
            IsSaving = _editor.IsSaving
        };
    }

    private bool ConfirmDiscardChanges(string question)
    {
        return !_editor.IsModified || MessageBox.Show(question, "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    internal void RefreshState()
    {
        CommandState = CaptureState();
        foreach (KeyValuePair<EditorToolbarCommand, ToolStripMenuItem> pair in _menuItems)
        {
            pair.Value.Enabled = CommandState.IsEnabled(pair.Key);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the commands after the editor has finished what it is doing: the events arrive in the middle of an edit,
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
        // The host focuses its child: the keys are for the editor, not for the menu.
        if (!_editor.Focused)
        {
            _editor.Focus();
        }
    }

    /// <summary>
    /// The menu is a WinForms window of its own and is not scaled with this control, so its images and font are
    /// scaled for the monitor it opens on.
    /// </summary>
    private void ScaleContextMenu()
    {
        if (!_editor.IsHandleCreated)
        {
            return;
        }
        int dpi = DpiScaling.GetWindowDpi(_editor.Handle);
        if (dpi == _contextMenuDpi)
        {
            return;
        }
        _contextMenuDpi = dpi;
        float imageScale = (float)dpi / DpiScaling.DesignDpi;
        int imageSize = Math.Max(1, (int)Math.Round(16f * imageScale));
        _contextMenu.ImageScalingSize = new Size(imageSize, imageSize);
        Font font = DpiScaling.ScaleFont(_menuFont, (float)dpi / DpiScaling.SystemDpi);
        Font previousFont = _scaledMenuFont;
        _scaledMenuFont = font;
        _contextMenu.Font = font;
        previousFont?.Dispose();
    }

    private void OnThemeChanged(object sender, EventArgs e)
    {
        ApplyTheme();
    }

    /// <summary>Paints the context menu in the colours of the application theme.</summary>
    private void ApplyTheme()
    {
        _palette = ToolbarPalette.FromCurrentTheme();
        _renderer = new ThemedToolStripRenderer(_palette);
        ApplyMenuTheme(_contextMenu);
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
            _scaledMenuFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
