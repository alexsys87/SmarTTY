using System.ComponentModel;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.Controls;

public partial class EditableTextBrowser : ReadonlySmartScrollBrowser, ISearchTarget, ITextReplaceHost
{
    public delegate void ModifiedChangedHandler(EditableTextBrowser editableTextBrowser, bool modified);

    public delegate void LineChangedHandler(EditableTextBrowser editableTextBrowser, int line, int column, int addedLength, int removedLength);

    public delegate void RangeChangedHandler(EditableTextBrowser editableTextBrowser, TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd);

    // The undo history and the saved state (made when first used: the base classes may ask before the constructor has run).
    private UndoHistory _history;

    /// <summary>The undo history of the edits and whether the text is saved.</summary>
    internal UndoHistory History => _history ?? (_history = new UndoHistory(OnModifiedChanged));

    private bool _readOnly;

    protected bool _useExistingLineEnding = true;

    /// <summary>Tab and Indent insert TabSize spaces instead of a tab character.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool InsertSpaces { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get
        {
            return _readOnly;
        }
        set
        {
            _readOnly = value;
        }
    }

    private EditableTextDocument Doc => Document;

    protected internal EditableTextDocument EditableDocument => Document;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public virtual bool IsModified
    {
        get
        {
            return History.IsModified;
        }
        set
        {
            History.IsModified = value;
        }
    }

    /// <summary>
    /// Identifies the current text for <see cref="MarkSaved"/>. A save that runs in the background takes it
    /// before reading the text, so edits made meanwhile keep the document modified.
    /// </summary>
    public object CurrentVersion => History.CurrentVersion;

    /// <summary>Records that the text of <paramref name="version"/> (from <see cref="CurrentVersion"/>) has been saved.</summary>
    public void MarkSaved(object version)
    {
        History.MarkSaved(version);
    }

    public event ModifiedChangedHandler ModifiedChanged;

    public event LineChangedHandler LineChanged;

    public event RangeChangedHandler RangeChanged;

    // The text range changes as ISearchTarget reports them: the searcher does not need the control in the arguments.
    private TextRangeChangedHandler _searchRangeChanged;

    event TextRangeChangedHandler ISearchTarget.RangeChanged
    {
        add
        {
            _searchRangeChanged += value;
        }
        remove
        {
            _searchRangeChanged -= value;
        }
    }

    string ISearchTarget.GetLineText(int line)
    {
        return EditableDocument.GetLineText(line);
    }

    string ISearchTarget.GetRangeText(TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        return EditableDocument.GetRangeText(start, end);
    }

    /// <summary>Runs several edits (ReplaceText calls) so that a single Undo reverts all of them.</summary>
    public void ExecuteAsSingleUndoStep(Action edits)
    {
        if (_readOnly)
        {
            return;
        }
        History.ExecuteAsSingleStep(edits);
    }

    /// <summary>
    /// Replaces every occurrence of <paramref name="searchText"/> as one undo step and returns how many were replaced.
    /// The caret stays where it was (clamped to its line).
    /// </summary>
    public int ReplaceAll(string searchText, string replacement, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return 0;
        }
        return ReplaceAll(TextMatcher.Literal(searchText, ignoreCase), replacement, TextDocument.TextPosition.Invalid, TextDocument.TextPosition.Invalid, out _);
    }

    /// <summary>
    /// Replaces every match of <paramref name="matcher"/> (a regular expression replacement may use $1, ${name}) as one
    /// undo step and returns how many were replaced. A valid <paramref name="scopeStart"/> and <paramref name="scopeEnd"/>
    /// limit it to that range, and <paramref name="newScopeEnd"/> is where the end of the range is afterwards.
    /// The caret stays where it was (clamped to its line).
    /// </summary>
    public int ReplaceAll(TextMatcher matcher, string replacement, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out TextDocument.TextPosition newScopeEnd)
    {
        newScopeEnd = scopeEnd;
        if (_readOnly || matcher == null)
        {
            return 0;
        }
        return Replacing.ReplaceAll(matcher, replacement, scopeStart, scopeEnd, out newScopeEnd);
    }

    /// <summary>There is an edit that <see cref="Undo"/> would revert.</summary>
    public bool CanUndo => !_readOnly && History.HasUndo;

    /// <summary>There is an undone edit that <see cref="Redo"/> would repeat.</summary>
    public bool CanRedo => !_readOnly && History.HasRedo;

    public void Undo()
    {
        if (!_readOnly && History.Undo())
        {
            Redraw();
        }
    }

    public void Redo()
    {
        if (!_readOnly)
        {
            History.Redo();
        }
        Redraw();
    }

    public EditableTextBrowser()
    {
        InitializeComponent();
    }

    public virtual void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
    {
        if (!_readOnly)
        {
            ReplaceTextCore(start, end, select, text);
        }
    }

    protected void ReplaceTextCore(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string replacement)
    {
        Replacing.Replace(start, end, select, replacement);
    }

    public void Indent(int levels)
    {
        if (_readOnly)
        {
            return;
        }
        History.Execute(new IndentCommand(this, levels));
    }

    protected virtual void OnLineChanged(int line, int column, int addedLength, int removedLength)
    {
        InvalidateWrapMap();
        if (line == CaretPosition.Line)
        {
            string text = EditableDocument.GetLineText(line);
            if (CaretPosition.Column > text.Length)
            {
                SetCaretColumn(text.Length);
            }
        }
        if (LineChanged != null)
        {
            LineChanged(this, line, column, addedLength, removedLength);
        }
        OnTextChangedOutsideCommand();
    }

    protected virtual void OnRangeChanged(TextDocument.TextPosition start, TextDocument.TextPosition oldEnd, TextDocument.TextPosition newEnd)
    {
        InvalidateWrapMap();
        if (RangeChanged != null)
        {
            RangeChanged(this, start, oldEnd, newEnd);
        }
        _searchRangeChanged?.Invoke(start, oldEnd, newEnd);
        OnTextChangedOutsideCommand();
    }

    private void OnTextChangedOutsideCommand()
    {
        // Edit commands decide IsModified when they finish; a change made any other way can never be
        // undone back to the saved text.
        if (!History.InEdit)
        {
            IsModified = true;
        }
    }

    protected virtual void OnModifiedChanged()
    {
        if (ModifiedChanged != null)
        {
            // IsModified is virtual: the remote file editor reports the state of its hex view from this event too.
            ModifiedChanged(this, IsModified);
        }
    }

    protected override void OnTextReplaced()
    {
        base.OnTextReplaced();
        // A new document: its saved state is the empty undo history, unless it was already modified.
        History.Reset();
    }
}
