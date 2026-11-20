using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

// Where the caret goes by a key is CodeEditing/CaretNavigator (stage 3C); this partial gives it what it reads of the view.
public partial class ReadonlyTextBrowser : ICaretNavigationHost
{
    // Made when it is first used: the base classes may ask before the constructor has run.
    private CaretNavigator _navigator;

    /// <summary>Where the caret goes by a key, and the column that Up, Down and the pages keep.</summary>
    internal CaretNavigator Navigator => _navigator ?? (_navigator = new CaretNavigator(this));

    TextDocument ICaretNavigationHost.Document => _document;

    int ICaretNavigationHost.VisibleRows => _visibleRows;

    bool ICaretNavigationHost.IsWrapping => IsWrapping;

    WrapMap ICaretNavigationHost.Wrap => WrapLayout;

    bool ICaretNavigationHost.IsWordChar(char ch)
    {
        return IsWordChar(ch);
    }

    void ICaretNavigationHost.OnMoveBeyondDocument(ref TextDocument.TextPosition textPosition, int direction)
    {
        OnMoveBeyondDocument(ref textPosition, direction);
    }
}
