namespace SmarTTY.CodeEditing;

/// <summary>Decides whether the text of a token is the one a jump is looking for.</summary>
public delegate bool TokenPredicate(string text);

/// <summary>
/// What the editor commands of <see cref="EditorCommands"/> do with the editor: the methods they call, and nothing else.
/// Implemented by the code editor control.
/// </summary>
public interface IEditorCommandTarget
{
    /// <summary>Shows the completion popup for the word the caret is in.</summary>
    void ShowCompletion();

    /// <summary>Tokenizes the document again (the folds are rebuilt with it).</summary>
    void Retokenize();

    /// <summary>
    /// Moves the caret (extending the selection when <paramref name="selectText"/> is set) to the next or previous token that
    /// has a pair and whose text the predicate accepts; false when there is none.
    /// </summary>
    bool MoveToToken(bool searchPrevious, bool isOpening, bool selectText, TokenPredicate callback);

    /// <summary>Expands the fold at the caret.</summary>
    void ExpandCurrentFold();

    /// <summary>Collapses the fold at the caret.</summary>
    void CollapseCurrentFold();

    /// <summary>Inserts a copy of the selected lines (or of the caret line) below them.</summary>
    void DuplicateLines();

    /// <summary>Moves the selected lines (or the caret line) one line up (-1) or down (1).</summary>
    void MoveLines(int direction);

    /// <summary>Deletes the selected lines (or the caret line).</summary>
    void DeleteLines();

    /// <summary>Comments out the selected lines (or the caret line), or uncomments them; false when the file has no comment syntax known.</summary>
    bool ToggleComment();

    /// <summary>Makes the text bigger or smaller by the given number of steps (a step is a tenth of its size).</summary>
    void ZoomBy(int steps);

    /// <summary>Shows the text in the size of the font again.</summary>
    void ResetZoom();

    /// <summary>Selects the word under the caret; with a selection, adds a cursor on its next occurrence.</summary>
    void AddNextOccurrence();

    /// <summary>Puts a cursor on every occurrence of the selected text (or of the word under the caret).</summary>
    void SelectAllOccurrences();

    /// <summary>Adds a cursor on the line above.</summary>
    void AddCursorAbove();

    /// <summary>Adds a cursor on the line below.</summary>
    void AddCursorBelow();
}
