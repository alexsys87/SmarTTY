using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

// The control as the view of its screen (Terminal/Emulation/TerminalScreen.cs): every member forwards to what the control already had. They are
// implemented explicitly, so that the public surface of the control is not changed.
public partial class TerminalEmulatorBase
{
    int ITerminalScreenView.VisibleRows => _visibleRows;

    int ITerminalScreenView.VisibleColumns => _visibleColumns;

    EditableTextDocument ITerminalScreenView.Document => base.EditableDocument;

    int ITerminalScreenView.TopLine
    {
        get => TopLine;
        set => TopLine = value;
    }

    bool ITerminalScreenView.AlwaysTrackLines => AlwaysTrackLines;

    bool ITerminalScreenView.HideMargins => HideMargins;

    TextDocument.TextPosition ITerminalScreenView.SelectionStart => SelectionStart;

    bool ITerminalScreenView.HideCaret
    {
        set => HideCaret = value;
    }

    bool ITerminalScreenView.TraceEnabled
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    void ITerminalScreenView.SetDocument(EditableTextDocument document)
    {
        SetDocument(document);
    }

    void ITerminalScreenView.SetCaret(TextDocument.TextPosition position)
    {
        SetCaret(position);
    }

    void ITerminalScreenView.ForceCaretUpdate()
    {
        ForceCaretUpdate();
    }

    void ITerminalScreenView.SetSelection(bool select, TextDocument.TextPosition position)
    {
        SetSelection(select, position);
    }

    void ITerminalScreenView.OnTextReplaced()
    {
        OnTextReplaced();
    }

    void ITerminalScreenView.UpdateMaxLineWidth(int line)
    {
        UpdateMaxLineWidth(line);
    }

    void ITerminalScreenView.UpdateLayout()
    {
        UpdateLayout();
    }

    void ITerminalScreenView.Redraw()
    {
        Redraw();
    }

    ILinkScanner ITerminalScreenView.CreateLinkScanner()
    {
        return CreateLinkScanner();
    }

    void ITerminalScreenView.Trace(string text)
    {
        Trace(text);
    }
}
