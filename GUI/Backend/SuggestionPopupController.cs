using SmarTTY.Terminal;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using VisualTTY;
using KeyEventArgs = System.Windows.Forms.KeyEventArgs;
using Point = System.Windows.Point;

namespace SmarTTY.GUI.Backend;

internal class SuggestionPopupController : INotifyPropertyChanged, ICompletionPopup
{
    public enum ShortcutButtonIcon
    {
        None,
        Open,
        Go
    }

    public class Item
    {
        internal CompletionItem Entry;

        public VirtualTerminalPopupItemIcon Icon => Entry.Icon;

        public string Text => Entry.Text;

        public string ExtraText => Entry.Description;

        public ShortcutButtonIcon ShortcutIcon
        {
            get
            {
                if (Entry.Summary is VirtualTerminalPopupSummary.FileOrDirectoryInfo fileOrDirectoryInfo)
                {
                    if (!fileOrDirectoryInfo.IsDirectory)
                    {
                        return ShortcutButtonIcon.Open;
                    }
                    return ShortcutButtonIcon.Go;
                }
                return ShortcutButtonIcon.None;
            }
        }

        public string ShortcutTip
        {
            get
            {
                if (Entry.Summary is VirtualTerminalPopupSummary.FileOrDirectoryInfo fileOrDirectoryInfo)
                {
                    if (!fileOrDirectoryInfo.IsDirectory)
                    {
                        return "Open in text editor";
                    }
                    return "Open in another tab";
                }
                return null;
            }
        }
    }

    private CompletionItem[] AllEntries;

    private MainWindowController _Controller;

    private CompletionPopupRequest _Args;

    private Popup _VirtualTerminalPopup;

    private Item _SelectedItem;

    private string _Filter;

    private int _SelectedIndex;

    private Item _MouseDownItem;

    private Exception _Exception;

    private readonly EventHandler _PopupClosedHandler;

    private const int PageSize = 10;

    public bool IsDarkTheme => _Controller.Settings.Theme == SmarTTYTheme.Dark;

    public string NoItemsHint
    {
        get
        {
            Item[] items = Items;
            if (items == null || items.Length == 0)
            {
                if (_Exception != null)
                {
                    return _Exception.Message;
                }
                if (AllEntries == null)
                {
                    return _Args.StatusText ?? "Loading...";
                }
                return "No suggestions matching '" + _Filter + "' found";
            }
            return null;
        }
    }

    public string Title => _Args.Title;

    public VirtualTerminalPopupIcon Icon => _Args.Icon;

    public Item SelectedItem
    {
        get
        {
            return _SelectedItem;
        }
        set
        {
            _SelectedItem = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SelectedItem"));
            if (value != null && value == _MouseDownItem)
            {
                ItemSelected?.Invoke(value.Entry, Keys.None);
            }
        }
    }

    public int SelectedIndex
    {
        get
        {
            return _SelectedIndex;
        }
        set
        {
            _SelectedIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SelectedIndex"));
        }
    }

    public Item[] Items { get; private set; }

    public bool IsOpen { get; private set; }

    public event Action<CompletionItem, Keys> ItemSelected;

    public event PropertyChangedEventHandler PropertyChanged;

    public SuggestionPopupController(MainWindowController mainWindowController, CompletionPopupRequest e, Popup popup)
    {
        AllEntries = e.Items;
        _Controller = mainWindowController;
        _Args = e;
        _VirtualTerminalPopup = popup;
        IsOpen = true;
        _PopupClosedHandler = (object sender, EventArgs args) =>
        {
            Detach();
        };
        popup.Closed += _PopupClosedHandler;
        UpdateFilteredItems();
    }

    /// <summary>
    /// The popup window is shared by all completions: once it has been closed or shows another list, this controller
    /// is closed for good. The handler is removed as well; every controller used to stay subscribed (and in memory)
    /// for the lifetime of the window, and an earlier one kept reporting IsOpen while the popup showed a newer list.
    /// </summary>
    internal void Detach()
    {
        IsOpen = false;
        _VirtualTerminalPopup.Closed -= _PopupClosedHandler;
    }

    public void OnItemMouseDown(Item item)
    {
        _MouseDownItem = item;
    }

    private Point PointToRelative(double x, double y)
    {
        return _Controller.PointFromScreen(new Point(x, y));
    }

    internal CustomPopupPlacement[] PlacePopup(Size popupSize, Size targetSize, Point offset)
    {
        return new CustomPopupPlacement[2]
        {
            new CustomPopupPlacement(PointToRelative(_Args.ScreenLocation.X, (double)_Args.ScreenLocation.Y - popupSize.Height), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(PointToRelative(_Args.ScreenLocation.X, _Args.ScreenLocation.Y + _Args.LineHeight), PopupPrimaryAxis.Vertical)
        };
    }

    public void UpdateFilteredItems()
    {
        string filter = _Filter;
        Items = (from ba2 in AllEntries ?? new CompletionItem[0]
                 where string.IsNullOrEmpty(filter) || ba2.Text.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) != -1
                 select MakeItem(ba2, filter)).ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Items"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("NoItemsHint"));
        UpdateIndex();
    }

    private Item MakeItem(CompletionItem e, string filter)
    {
        return new Item
        {
            Entry = e
        };
    }

    public void SetFilter(string filter)
    {
        _Filter = filter;
        UpdateFilteredItems();
        UpdateIndex();
    }

    private void UpdateIndex()
    {
        if (Items.Length == 0)
        {
            SelectedIndex = -1;
            return;
        }
        int selectedIndex = -1;
        for (int itemIndex = 0; itemIndex < Items.Length; itemIndex++)
        {
            if (Items[itemIndex].Text.StartsWith(_Filter ?? ""))
            {
                selectedIndex = itemIndex;
                break;
            }
        }
        SelectedIndex = selectedIndex;
    }

    public void Close()
    {
        // Closing an old completion must not close the list of the current one.
        if (IsOpen && ((FrameworkElement)_VirtualTerminalPopup).DataContext == this)
        {
            _VirtualTerminalPopup.IsOpen = false;
        }
        Detach();
    }

    public bool TryHandleKeyEvent(KeyEventArgs e)
    {
        if (!IsOpen)
        {
            return false;
        }
        switch (e.KeyCode)
        {
            case Keys.Up:
                if (SelectedIndex > 0)
                {
                    SelectedIndex--;
                }
                return true;
            case Keys.Down:
                // The last item is Items.Length - 1: the index used to run one past it.
                if (SelectedIndex < Items.Length - 1)
                {
                    SelectedIndex++;
                }
                return true;
            case Keys.Prior:
                if (Items.Length > 0)
                {
                    SelectedIndex = Math.Max(0, SelectedIndex - PageSize);
                }
                return true;
            case Keys.Next:
                if (Items.Length > 0)
                {
                    SelectedIndex = Math.Min(Items.Length - 1, Math.Max(0, SelectedIndex) + PageSize);
                }
                return true;
            case Keys.Tab:
            case Keys.Return:
                if (SelectedItem != null)
                {
                    ItemSelected?.Invoke(SelectedItem.Entry, e.KeyCode);
                    return true;
                }
                break;
        }
        return false;
    }

    public void DisplayException(Exception exception)
    {
        _Exception = exception;
        AllEntries = null;
        UpdateFilteredItems();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("NoItemsHint"));
    }

    public void SetItemList(CompletionItem[] items)
    {
        AllEntries = items;
        UpdateFilteredItems();
    }

    public CompletionItem GetSelectedItem(bool onlyIfPrefixMatch)
    {
        Item selectedItem = SelectedItem;
        if (selectedItem == null)
        {
            return default;
        }
        if (onlyIfPrefixMatch && (string.IsNullOrEmpty(_Filter) || !selectedItem.Entry.Text.StartsWith(_Filter)))
        {
            return default;
        }
        return selectedItem.Entry;
    }
}
