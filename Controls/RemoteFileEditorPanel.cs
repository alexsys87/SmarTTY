using SmarTTY.CodeEditing;
using SmarTTY.Forms;
using SmarTTY.GUI.Styles;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// A remote file editor with its WinForms text view and its context menu. The commands and their state are
/// <see cref="Commands"/> (<see cref="RemoteFileEditorCommands"/>): the WPF tool bar above the editor, this context menu and
/// the keys run them there, and the WPF status bar below the editor reads the state of the editor when its
/// <see cref="RemoteFileEditorCommands.StateChanged"/> is raised.
/// </summary>
internal sealed class RemoteFileEditorPanel : UserControl
{
    private readonly RemoteFileEditor _editor;

    // The WinForms text view of the editor, which this panel hosts.
    private readonly RemoteTextView _view;

    private readonly ContextMenuStrip _contextMenu;

    private readonly Dictionary<EditorToolbarCommand, ToolStripMenuItem> _menuItems = new Dictionary<EditorToolbarCommand, ToolStripMenuItem>();

    // The Text submenu of the context menu: trailing blanks, sorting, duplicates, indentation, the final newline.
    private readonly ToolStripMenuItem _textMenu;

    private readonly Dictionary<LineTransformKind, ToolStripMenuItem> _transformItems = new Dictionary<LineTransformKind, ToolStripMenuItem>();

    // The submenus of the hex view: highlights, Copy As, byte operations and the view options.
    private readonly HexEditorMenu _hexMenu;

    // The unscaled menu font, and the font for the current monitor (null until the menu is opened).
    private readonly Font _menuFont;

    private Font _scaledMenuFont;

    private ToolbarPalette _palette;

    private ToolStripRenderer _renderer;

    private int _contextMenuDpi;

    /// <summary>The commands of the editor and their state.</summary>
    internal RemoteFileEditorCommands Commands { get; }

    public RemoteFileEditorPanel(RemoteFileEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _view = editor.TextControl ?? throw new ArgumentException("The panel hosts the WinForms text view of the editor.", nameof(editor));
        Commands = new RemoteFileEditorCommands(editor, PostRefresh);
        // First, so that the menu shows the new state before the tool bar and the status bar are told.
        Commands.StateChanged += (sender, e) => UpdateMenuItems();
        SuspendLayout();

        _contextMenu = new ContextMenuStrip();
        foreach (EditorToolbarCommand? command in EditorCommandCatalog.ContextMenuLayout)
        {
            _contextMenu.Items.Add(command.HasValue ? (ToolStripItem)CreateMenuItem(command.Value) : new ToolStripSeparator());
        }
        _textMenu = new ToolStripMenuItem("Text");
        foreach (LineTransformKind kind in new[]
        {
            LineTransformKind.TrimTrailingWhitespace,
            LineTransformKind.EnsureFinalNewline,
            LineTransformKind.SortAscending,
            LineTransformKind.SortDescending,
            LineTransformKind.RemoveDuplicateLines,
            LineTransformKind.IndentationToSpaces,
            LineTransformKind.IndentationToTabs
        })
        {
            LineTransformKind current = kind;
            ToolStripMenuItem item = new ToolStripMenuItem(LineTransforms.Title(kind));
            item.Click += (sender, e) => Commands.RunTransform(current);
            _transformItems[kind] = item;
            _textMenu.DropDownItems.Add(item);
        }
        // After Word Wrap, with the other commands about how the text looks and is laid out.
        _contextMenu.Items.Insert(_contextMenu.Items.IndexOf(_menuItems[EditorToolbarCommand.WordWrap]) + 1, _textMenu);
        // After Hex View, shown in the hex view only.
        _hexMenu = new HexEditorMenu(
            _editor.HexEditor,
            kind => Commands.RequestHexDialog(kind),
            () =>
            {
                _editor.FocusActiveView();
                RefreshState();
            });
        int hexMenuIndex = _contextMenu.Items.IndexOf(_menuItems[EditorToolbarCommand.ToggleHex]) + 1;
        foreach (ToolStripMenuItem menu in _hexMenu.Menus)
        {
            _contextMenu.Items.Insert(hexMenuIndex++, menu);
        }
        _menuFont = _contextMenu.Font;
        _contextMenu.Opening += (sender, e) =>
        {
            ScaleContextMenu();
            ApplyMenuTheme(_contextMenu);
            RefreshState();
        };
        _view.ContextMenuStrip = _contextMenu;
        // A hex view given to the editor (the WPF one of the tab) has the context menu of the tab.
        if (_editor.HexEditorControl != null)
        {
            _editor.HexEditorControl.ContextMenuStrip = _contextMenu;
        }

        _view.Dock = DockStyle.Fill;
        Controls.Add(_view);
        ActiveControl = _view;
        ResumeLayout(performLayout: true);

        // What the commands depend on in the text view: the selection, the text and its undo history (the commands follow the rest).
        _view.SelectionChanged += (editorControl, previousCaret, currentCaret, hasSelection) => Commands.ScheduleRefresh();
        _view.ModifiedChanged += (editorControl, modified) => Commands.ScheduleRefresh();
        _view.RangeChanged += (editorControl, start, oldEnd, newEnd) => Commands.ScheduleRefresh();
        _view.LineChanged += (editorControl, line, column, addedLength, removedLength) => Commands.ScheduleRefresh();
        _view.KeyUp += (sender, e) => Commands.ScheduleRefresh();
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
            ShortcutKeyDisplayString = description.Shortcut,
            CheckOnClick = false
        };
        Icons.Apply(menuItem, description.IconName);
        menuItem.Click += (sender, e) => Commands.RunCommand(command);
        _menuItems[command] = menuItem;
        return menuItem;
    }

    /// <summary>Reads the state of the commands again (the menu, the tool bar and the status bar show it).</summary>
    internal void RefreshState()
    {
        Commands.RefreshState();
    }

    private void UpdateMenuItems()
    {
        EditorCommandState state = Commands.CommandState;
        foreach (KeyValuePair<EditorToolbarCommand, ToolStripMenuItem> pair in _menuItems)
        {
            pair.Value.Enabled = state.IsEnabled(pair.Key);
            pair.Value.Checked = state.IsChecked(pair.Key);
            pair.Value.Visible = state.IsVisible(pair.Key);
        }
        _textMenu.Enabled = !state.ReadOnly && !state.HexMode;
        _hexMenu.Refresh(state.HexMode);
        foreach (KeyValuePair<LineTransformKind, ToolStripMenuItem> pair in _transformItems)
        {
            pair.Value.Enabled = state.IsEnabled(pair.Key);
        }
    }

    // The refresh of the commands runs after the editor has finished what it is doing (see RemoteFileEditorCommands.ScheduleRefresh).
    private bool PostRefresh(Action refresh)
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return false;
        }
        BeginInvoke((MethodInvoker)(() =>
        {
            if (!IsDisposed)
            {
                refresh();
            }
        }));
        return true;
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
        if (!_view.Focused && !_editor.HexEditor.Focused)
        {
            _editor.FocusActiveView();
        }
    }

    /// <summary>
    /// The menu is a WinForms window of its own and is not scaled with this control, so its images and font are
    /// scaled for the monitor it opens on.
    /// </summary>
    private void ScaleContextMenu()
    {
        if (!_view.IsHandleCreated)
        {
            return;
        }
        int dpi = DpiScaling.GetWindowDpi(_view.Handle);
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
        _textMenu.DropDown.Font = font;
        _textMenu.DropDown.ImageScalingSize = new Size(imageSize, imageSize);
        foreach (ToolStripDropDown dropDown in _hexMenu.DropDowns)
        {
            dropDown.Font = font;
            dropDown.ImageScalingSize = new Size(imageSize, imageSize);
        }
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
        ApplyMenuTheme(_textMenu.DropDown);
        foreach (ToolStripDropDown dropDown in _hexMenu.DropDowns)
        {
            ApplyMenuTheme(dropDown);
        }
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
            _hexMenu.Dispose();
            _scaledMenuFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
