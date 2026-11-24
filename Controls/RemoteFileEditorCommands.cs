using SmarTTY.CodeEditing;
using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The commands of a remote file editor tab and their state: what the tool bar, the context menus and the keys run (<see cref="RunCommand"/>,
/// <see cref="RunTransform"/>), what is enabled, checked and shown (<see cref="CommandState"/>), and when it is read again
/// (<see cref="ScheduleRefresh"/>, <see cref="StateChanged"/>). The commands that need the main window (saving, the search bar, the Go to Line
/// dialog) are raised as events; the others act on the editor, its text view and its hex view. Moved as it was from
/// the WinForms <c>RemoteFileEditorPanel</c> (stage 3E-4), so that the WPF tab runs the same commands; the panel went with the WinForms editor in
/// stage 3E-5.
/// </summary>
internal sealed class RemoteFileEditorCommands
{
    private readonly RemoteFileEditor _editor;

    private readonly IRemoteTextView _text;

    // Runs an action later on the thread of the tab; false when the tab cannot take it (no window yet, or closed).
    private readonly Func<Action, bool> _post;

    private bool _refreshPending;

    /// <summary>The Save button or menu item was used.</summary>
    public event EventHandler SaveRequested;

    public event EventHandler FindRequested;

    public event EventHandler ReplaceRequested;

    public event EventHandler GoToLineRequested;

    /// <summary>The Show Changes button or menu item was used: the window with the differences is the main window's.</summary>
    public event EventHandler ShowChangesRequested;

    /// <summary>The Run in Terminal button, menu item or F5 was used: the terminal tabs belong to the session.</summary>
    public event EventHandler RunRequested;

    /// <summary>The Follow Log button or menu item was used: the terminal tab and the question belong to the main window.</summary>
    public event EventHandler FollowLogRequested;

    /// <summary>The toolbar or the context menu switched the invisible-character marks on or off.</summary>
    public event EventHandler ShowAllCharactersChanged;

    /// <summary>The toolbar or the context menu switched the word wrap on or off.</summary>
    public event EventHandler WordWrapChanged;

    /// <summary>The byte order, the groups, the row width, the inspector or the colours of the hex view changed.</summary>
    public event EventHandler HexViewOptionsChanged;

    /// <summary>A command of the hex view needs a dialog of the main window (a colour, a byte pattern, the checksums).</summary>
    public event EventHandler<HexDialogRequestedEventArgs> HexDialogRequested;

    /// <summary>The enabled state of the commands changed (for the tool bar and the menus).</summary>
    internal event EventHandler StateChanged;

    // For RemoteFileEditorStatusSource: the event as two calls.
    internal void AddStateChanged(EventHandler handler)
    {
        StateChanged += handler;
    }

    internal void RemoveStateChanged(EventHandler handler)
    {
        StateChanged -= handler;
    }

    internal EditorCommandState CommandState { get; private set; }

    /// <param name="post">Runs an action later on the thread of the tab; false when the tab cannot take it now (then nothing is refreshed).</param>
    public RemoteFileEditorCommands(RemoteFileEditor editor, Func<Action, bool> post)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _text = editor.TextView;
        _post = post ?? throw new ArgumentNullException(nameof(post));
        // What the commands depend on besides the text view (its changes are told by the tab through ScheduleRefresh): the saved state and the load.
        _editor.SavingChanged += (sender, e) => ScheduleRefresh();
        _editor.LoadCompleted += (sender, e) => ScheduleRefresh();
        _editor.FormatChanged += (sender, e) => ScheduleRefresh();
        _editor.HexModeChanged += (sender, e) => ScheduleRefresh();
        _editor.HexEditor.StateChanged += (sender, e) => ScheduleRefresh();
        // Keys handled by the hex control itself (it has the focus while it is shown).
        _editor.HexEditor.SaveRequested += (sender, e) => SaveRequested?.Invoke(this, EventArgs.Empty);
        _editor.HexEditor.FindRequested += (sender, e) => FindRequested?.Invoke(this, EventArgs.Empty);
        _editor.HexEditor.GoToOffsetRequested += (sender, e) => GoToLineRequested?.Invoke(this, EventArgs.Empty);
        _editor.HexEditor.ViewOptionsChanged += (sender, e) =>
        {
            ScheduleRefresh();
            HexViewOptionsChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>A command of the hex view asks for a dialog of the main window.</summary>
    public void RequestHexDialog(HexDialogKind kind)
    {
        HexDialogRequested?.Invoke(this, new HexDialogRequestedEventArgs(kind));
    }

    /// <summary>Runs a command of the Text menu.</summary>
    public void RunTransform(LineTransformKind kind)
    {
        if (!CaptureState().IsEnabled(kind))
        {
            RefreshState();
            return;
        }
        try
        {
            _text.ApplyLineTransform(kind);
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
        _editor.FocusActiveView();
        RefreshState();
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
                    if (_editor.IsHexMode)
                    {
                        _editor.HexEditor.Undo();
                    }
                    else
                    {
                        _text.Undo();
                    }
                    break;
                case EditorToolbarCommand.Redo:
                    if (_editor.IsHexMode)
                    {
                        _editor.HexEditor.Redo();
                    }
                    else
                    {
                        _text.Redo();
                    }
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
                case EditorToolbarCommand.FollowLog:
                    focusEditor = false;
                    FollowLogRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case EditorToolbarCommand.RunFile:
                    focusEditor = false;
                    RunRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case EditorToolbarCommand.ShowChanges:
                    focusEditor = false;
                    ShowChangesRequested?.Invoke(this, EventArgs.Empty);
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
                case EditorToolbarCommand.ShowAllCharacters:
                    _text.ShowAllCharacters = !_text.ShowAllCharacters;
                    ShowAllCharactersChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case EditorToolbarCommand.WordWrap:
                    _text.WordWrap = !_text.WordWrap;
                    WordWrapChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case EditorToolbarCommand.ToggleHex:
                    _editor.ToggleHexMode();
                    break;
                case EditorToolbarCommand.DataInspector:
                    _editor.HexEditor.ShowDataInspector = !_editor.HexEditor.ShowDataInspector;
                    break;
            }
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
        if (focusEditor)
        {
            _editor.FocusActiveView();
        }
        RefreshState();
    }

    internal EditorCommandState CaptureState()
    {
        bool hex = _editor.IsHexMode;
        if (hex)
        {
            IHexEditorView hexEditor = _editor.HexEditor;
            return new EditorCommandState
            {
                ReadOnly = hexEditor.ReadOnly,
                HasSelection = hexEditor.HasSelection,
                HasText = hexEditor.Length > 0,
                CanUndo = hexEditor.CanUndo,
                CanRedo = hexEditor.CanRedo,
                IsModified = _editor.IsModified,
                IsNewFile = _editor.IsNewFile,
                IsSaving = _editor.IsSaving,
                HexMode = true,
                ShowDataInspector = hexEditor.ShowDataInspector,
                CanRun = _editor.CanRunInTerminal,
                CanFollow = _editor.CanFollowLog,
                CanUseHex = _editor.CanToggleHexMode,
                ShowAllCharacters = _text.ShowAllCharacters,
                WordWrap = _text.WordWrap
            };
        }
        int lineCount = _text.LineCount;
        return new EditorCommandState
        {
            ReadOnly = _text.ReadOnly,
            HasLineSelection = _text.HasLineSelection,
            HasSelection = _text.HasSelection && _text.SelectionStart != _text.SelectionEnd,
            HasText = lineCount > 1 || (lineCount == 1 && _text.Document.GetLineText(0).Length > 0),
            CanUndo = _text.CanUndo,
            CanRedo = _text.CanRedo,
            IsModified = _editor.IsModified,
            IsNewFile = _editor.IsNewFile,
            IsSaving = _editor.IsSaving,
            HexMode = false,
            CanRun = _editor.CanRunInTerminal,
            CanFollow = _editor.CanFollowLog,
            CanUseHex = _editor.CanToggleHexMode,
            ShowAllCharacters = _text.ShowAllCharacters,
            WordWrap = _text.WordWrap
        };
    }

    private bool ConfirmDiscardChanges(string question)
    {
        return !_editor.IsModified || ThemedMessageBox.Show(question, "SmarTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    /// <summary>Reads the state of the commands again and tells the tool bar and the menus.</summary>
    internal void RefreshState()
    {
        CommandState = CaptureState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the commands after the editor has finished what it is doing: the events arrive in the middle of an edit,
    /// before the undo history is complete, and many of them (a mouse drag) come in a row.
    /// </summary>
    internal void ScheduleRefresh()
    {
        if (_refreshPending)
        {
            return;
        }
        _refreshPending = _post(() =>
        {
            _refreshPending = false;
            RefreshState();
        });
    }
}
