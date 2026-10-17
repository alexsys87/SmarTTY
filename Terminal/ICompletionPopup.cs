using System;
using System.Windows.Forms;

namespace SmarTTY.Terminal;

public interface ICompletionPopup
{
    bool IsOpen { get; }

    event Action<CompletionItem, Keys> ItemSelected;

    void Close();

    void SetFilter(string text);

    bool TryHandleKeyEvent(KeyEventArgs e);

    void DisplayException(Exception exception);

    void SetItemList(CompletionItem[] items);

    CompletionItem GetSelectedItem(bool onlyIfPrefixMatch);
}
