using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;

namespace SmarTTY.Controls;

/// <summary>
/// <see cref="IEditorStatusSource"/> of a <see cref="RemoteFileEditor"/> inside its <see cref="RemoteFileEditorPanel"/>: every member is the member of the editor
/// (or of its hex view) that the status bar read directly before. The panel tells when to read again.
/// </summary>
internal sealed class RemoteFileEditorStatusSource : IEditorStatusSource
{
    private readonly RemoteFileEditor _editor;

    private readonly Action<EventHandler> _addStateChanged;

    private readonly Action<EventHandler> _removeStateChanged;

    public RemoteFileEditorStatusSource(RemoteFileEditor editor, RemoteFileEditorPanel panel)
        : this(editor, (panel ?? throw new ArgumentNullException(nameof(panel))).AddStateChanged, panel.RemoveStateChanged)
    {
    }

    /// <summary>For a host that has the event of the panel in another form (the checks of the editor under Mono cannot make the panel, which is WPF-based).</summary>
    internal RemoteFileEditorStatusSource(RemoteFileEditor editor, Action<EventHandler> addStateChanged, Action<EventHandler> removeStateChanged)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _addStateChanged = addStateChanged ?? throw new ArgumentNullException(nameof(addStateChanged));
        _removeStateChanged = removeStateChanged ?? throw new ArgumentNullException(nameof(removeStateChanged));
    }

    public event EventHandler StateChanged
    {
        add
        {
            _addStateChanged(value);
        }
        remove
        {
            _removeStateChanged(value);
        }
    }

    public event EventHandler CurrentLanguageChanged
    {
        add
        {
            _editor.CurrentLanguageChanged += value;
        }
        remove
        {
            _editor.CurrentLanguageChanged -= value;
        }
    }

    public string RemotePath => _editor.RemotePath;

    public bool IsPreview => _editor.IsPreview;

    public bool IsReadOnlyMode => _editor.IsReadOnlyMode;

    public bool IsSaving => _editor.IsSaving;

    public bool IsModified => _editor.IsModified;

    public bool IsNewFile => _editor.IsNewFile;

    public bool ReadOnly => _editor.TextView.ReadOnly;

    public bool CanReopenWithEncoding => _editor.CanReopenWithEncoding;

    public bool IsHexMode => _editor.IsHexMode;

    public IHexViewStatus HexView => _editor.HexEditor;

    public TextDocument.TextPosition CaretPosition => _editor.TextView.CaretPosition;

    public bool HasSelection => _editor.TextView.HasSelection;

    public TextDocument.TextPosition SelectionStart => _editor.TextView.SelectionStart;

    public TextDocument.TextPosition SelectionEnd => _editor.TextView.SelectionEnd;

    public string GetLineText(int line)
    {
        return _editor.TextView.Document.GetLineText(line);
    }

    public string GetEffectiveLineTerminator(int line)
    {
        return _editor.TextView.Document.GetEffectiveLineTerminator(line);
    }

    public string FirstLine => _editor.TextView.Document?.GetLineText(0);

    public TextFileEncoding FileEncoding => _editor.FileEncoding;

    public LineEndingKind LineEndings => _editor.LineEndings;

    public SyntaxHighlighting.Language CurrentLanguage => _editor.CurrentLanguage;

    public SyntaxHighlighting.Language? SyntaxOverride => _editor.SyntaxOverride;

    public void SetSaveEncoding(TextFileEncoding encoding)
    {
        _editor.SetSaveEncoding(encoding);
    }

    public void ReopenWithEncoding(TextFileEncoding encoding)
    {
        _editor.ReopenWithEncoding(encoding);
    }

    public void SetSyntax(SyntaxHighlighting.Language? language)
    {
        _editor.SetSyntax(language);
    }

    public void SetLineEnding(TextDocument.LineEnding lineEnding)
    {
        _editor.SetLineEnding(lineEnding);
    }

    public void FocusActiveView()
    {
        _editor.FocusActiveView();
    }
}
