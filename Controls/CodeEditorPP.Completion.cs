using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using SmarTTY.TextEditing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>The word list and the parameter hint of the editor: the rules are <see cref="EditorCompletion"/>, the windows are made here.</summary>
public partial class CodeEditorPP : IEditorCompletionHost
{
    /// <summary>The completion of this editor (made when first used: the base class constructors may already type or move the caret).</summary>
    internal EditorCompletion Completion => _completion ?? (_completion = new EditorCompletion(this));

    EditableTextDocument IEditorCompletionHost.Document => base.EditableDocument;

    TextDocument.TextPosition IEditorCompletionHost.CaretPosition => CaretPosition;

    string IEditorCompletionHost.CurrentText => CurrentText;

    RgbColor IEditorCompletionHost.SuggestionBackColor => SuggestionBackColor.ToRgb();

    ISuggestionPopupFactory IEditorCompletionHost.CreatePopupFactory()
    {
        return new SuggestionPopupFactory(this);
    }

    IHintView IEditorCompletionHost.CreateHintView()
    {
        Control control = null;
        if (!IsWindows())
        {
            control = this;
        }
        return HintViewFactory.Create(this, control);
    }

    void IEditorCompletionHost.FocusView()
    {
        Focus();
    }

    bool IEditorCompletionHost.IsWordSeparator(char ch)
    {
        return IsWordSeparator(ch);
    }
}
