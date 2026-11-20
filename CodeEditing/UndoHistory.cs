using System;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>An edit that can be undone and done again (the view's edit commands are such steps).</summary>
public interface IUndoStep
{
    /// <summary>Makes the edit (the first time and on Redo).</summary>
    void Execute();

    /// <summary>Takes the edit back.</summary>
    void Undo();
}

/// <summary>Several edits undone and redone as one step (e.g. Replace All).</summary>
public sealed class CompoundUndoStep : IUndoStep
{
    /// <summary>The edits, in the order they were made.</summary>
    public List<IUndoStep> Steps { get; } = new List<IUndoStep>();

    public void Execute()
    {
        foreach (IUndoStep step in Steps)
        {
            step.Execute();
        }
    }

    public void Undo()
    {
        for (int i = Steps.Count - 1; i >= 0; i--)
        {
            Steps[i].Undo();
        }
    }
}

/// <summary>
/// The undo history of the text view and whether its text is saved: the edits that can be undone and redone, the step the text matched when it was
/// last saved (undoing or redoing back to it clears the modified state), the edits that run as one step, and a change made outside an edit, after
/// which the saved text can never come back by undoing. It is what <c>Controls/EditableTextBrowser</c> kept in <c>_undoStack</c>, <c>_redoStack</c>,
/// <c>_savedUndoStep</c>, <c>_commandDepth</c>, <c>_compoundCommand</c> and <c>_modified</c> and did in <c>IsModified</c>, <c>MarkSaved</c>,
/// <c>RunCommand</c>, <c>ExecuteCommand</c>, <c>ExecuteAsSingleUndoStep</c>, <c>Undo</c>, <c>Redo</c>, the test of <c>OnTextChangedOutsideCommand</c>
/// and <c>OnTextReplaced</c>, moved here as it was (stage 3C); the view keeps the edit commands, read-only, the redraws and the events.
/// </summary>
public sealed class UndoHistory
{
    // A marker no undo step can match: the text was never saved as it is now.
    private static readonly object NeverSaved = new object();

    private readonly Stack<IUndoStep> _undoStack = new Stack<IUndoStep>();

    private readonly Stack<IUndoStep> _redoStack = new Stack<IUndoStep>();

    private readonly Action _modifiedChanged;

    private bool _modified;

    // The undo step the text matched when it was last saved (null: the empty undo history).
    private object _savedUndoStep;

    // > 0 while an edit runs: its own change notifications must not overwrite the saved marker.
    private int _commandDepth;

    // Collects the edits of ExecuteAsSingleStep.
    private CompoundUndoStep _compound;

    /// <param name="modifiedChanged">Called when <see cref="IsModified"/> changes.</param>
    public UndoHistory(Action modifiedChanged)
    {
        _modifiedChanged = modifiedChanged ?? throw new ArgumentNullException(nameof(modifiedChanged));
    }

    /// <summary>The text is not the saved one. Setting it marks the text as never saved (true) or as saved now (false).</summary>
    public bool IsModified
    {
        get
        {
            return _modified;
        }
        set
        {
            _savedUndoStep = value ? NeverSaved : CurrentVersion;
            SetModified(value);
        }
    }

    /// <summary>Identifies the current text for <see cref="MarkSaved"/> (the newest undo step, or null).</summary>
    public object CurrentVersion => (_undoStack.Count > 0) ? _undoStack.Peek() : null;

    /// <summary>Records that the text of <paramref name="version"/> (from <see cref="CurrentVersion"/>) has been saved.</summary>
    public void MarkSaved(object version)
    {
        _savedUndoStep = version;
        SetModified(CurrentVersion != version);
    }

    /// <summary>There is an edit to undo.</summary>
    public bool HasUndo => _undoStack.Count > 0;

    /// <summary>There is an undone edit to do again.</summary>
    public bool HasRedo => _redoStack.Count > 0;

    /// <summary>Makes an edit and records it (inside <see cref="ExecuteAsSingleStep"/> as a part of that step); the redo history is dropped.</summary>
    public void Execute(IUndoStep step)
    {
        if (_compound != null)
        {
            step.Execute();
            _compound.Steps.Add(step);
            return;
        }
        Run(() =>
        {
            step.Execute();
            if (_redoStack.Count > 0)
            {
                _redoStack.Clear();
            }
            _undoStack.Push(step);
        });
    }

    /// <summary>Runs several edits so that a single undo reverts all of them; inside another such run they are a part of it.</summary>
    public void ExecuteAsSingleStep(Action edits)
    {
        if (_compound != null)
        {
            edits();
            return;
        }
        CompoundUndoStep compound = new CompoundUndoStep();
        Run(() =>
        {
            _compound = compound;
            try
            {
                edits();
            }
            finally
            {
                _compound = null;
                if (compound.Steps.Count > 0)
                {
                    _redoStack.Clear();
                    _undoStack.Push(compound);
                }
            }
        });
    }

    /// <summary>Undoes the newest edit; false when there is none.</summary>
    public bool Undo()
    {
        if (_undoStack.Count == 0)
        {
            return false;
        }
        Run(() =>
        {
            IUndoStep step = _undoStack.Pop();
            step.Undo();
            _redoStack.Push(step);
        });
        return true;
    }

    /// <summary>Does the newest undone edit again; false when there is none.</summary>
    public bool Redo()
    {
        if (_redoStack.Count == 0)
        {
            return false;
        }
        Run(() =>
        {
            IUndoStep step = _redoStack.Pop();
            step.Execute();
            _undoStack.Push(step);
        });
        return true;
    }

    /// <summary>
    /// An edit is running. A change of the text made while none runs can never be undone back to the saved text: the view then sets its
    /// (overridable) modified state.
    /// </summary>
    public bool InEdit => _commandDepth > 0;

    /// <summary>A new document: the history is empty, and its saved state is the empty history unless the text was modified.</summary>
    public void Reset()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _savedUndoStep = _modified ? NeverSaved : null;
    }

    private void SetModified(bool modified)
    {
        if (modified != _modified)
        {
            _modified = modified;
            _modifiedChanged();
        }
    }

    private void Run(Action action)
    {
        _commandDepth++;
        try
        {
            action();
        }
        finally
        {
            _commandDepth--;
        }
        SetModified(CurrentVersion != _savedUndoStep);
    }
}
