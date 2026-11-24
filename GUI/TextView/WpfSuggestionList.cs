using SmarTTY.Completion;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SmarTTY.GUI.TextView;

/// <summary>What the word list of the WPF text view asks of the editor it is shown for.</summary>
internal interface IWpfSuggestionHost
{
    /// <summary>The element the list is placed against.</summary>
    UIElement PlacementTarget { get; }

    /// <summary>The caret in the coordinates of <see cref="PlacementTarget"/> (device-independent pixels).</summary>
    Rect CaretBounds { get; }

    /// <summary>The colour of the text of the items.</summary>
    RgbColor ItemForeColor { get; }

    /// <summary>The font of the editor (the list shows the names in it).</summary>
    FontFamily ItemFontFamily { get; }

    double ItemFontSize { get; }

    /// <summary>An item was accepted (with the key, or <see cref="VirtualKey.None"/> for a double click).</summary>
    void AcceptSuggestion(ISuggestion suggestion, VirtualKey key);
}

/// <summary>
/// The word list of the completion on WPF, as the WinForms list (<c>Forms/SuggestionListForm</c> with <c>Controls/SuggestionListView</c>) is:
/// the item a typed word selects and the items it puts on top are <see cref="SuggestionListMatch.Find"/>, the keys <see cref="SuggestionListMatch.MapKey"/>.
/// It never takes the keyboard: the editor keeps it and hands the list its keys (<see cref="HandleKey"/>), so typing goes on into the text.
/// Placed under the caret (above it when there is no room), as wide as the longest name up to a limit, at most ten rows high; the hint of the
/// selected item (its signature) is shown beside it.
/// </summary>
internal sealed class WpfSuggestionList : ISuggestionPopup
{
    private const double MaxWidth = 400;

    private const int MaxRows = 10;

    private readonly IWpfSuggestionHost _host;

    private readonly Popup _popup;

    private readonly ListBox _list;

    private readonly Popup _hintPopup;

    private readonly TextBlock _hintText;

    private readonly TextBlock _hintDescription;

    // The items in the order of the source; the list shows them in this order unless a typed word put some on top.
    private readonly List<ISuggestion> _allItems = new List<ISuggestion>();

    private readonly List<ListBoxItem> _containers = new List<ListBoxItem>();

    private bool _isFiltered;

    public WpfSuggestionList(IWpfSuggestionHost host, ISuggestionSource source, bool allowSorting)
    {
        _host = host;
        _list = new ListBox
        {
            Focusable = false,
            BorderThickness = new Thickness(0),
            FontFamily = host.ItemFontFamily,
            FontSize = host.ItemFontSize
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (sender, e) => ShowItemHint();
        // The items cannot take the keyboard (the editor keeps it), and a WPF item selects itself only when it can: the click selects here,
        // and a double click accepts, as in the WinForms list.
        _list.PreviewMouseLeftButtonDown += (sender, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) is ListBoxItem item)
            {
                _list.SelectedItem = item;
                if (e.ClickCount == 2)
                {
                    AcceptSelected();
                }
                e.Handled = true;
            }
        };
        Border border = new Border { BorderThickness = new Thickness(1), Child = _list };
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.BorderStrong");
        _popup = new Popup
        {
            Child = border,
            PlacementTarget = host.PlacementTarget,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = false,
            Focusable = false
        };
        _hintText = new TextBlock { TextWrapping = TextWrapping.NoWrap };
        _hintDescription = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 400, Margin = new Thickness(0, 4, 0, 0) };
        _hintText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        _hintDescription.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        StackPanel hintPanel = new StackPanel { Margin = new Thickness(6, 3, 6, 3) };
        hintPanel.Children.Add(_hintText);
        hintPanel.Children.Add(_hintDescription);
        Border hintBorder = new Border { BorderThickness = new Thickness(1), Child = hintPanel };
        hintBorder.SetResourceReference(Border.BackgroundProperty, "Theme.PopupBackground");
        hintBorder.SetResourceReference(Border.BorderBrushProperty, "Theme.BorderStrong");
        _hintPopup = new Popup
        {
            Child = hintBorder,
            PlacementTarget = border,
            Placement = PlacementMode.Right,
            StaysOpen = true,
            Focusable = false
        };
        List<ISuggestion> items = new List<ISuggestion>(source.Items);
        if (allowSorting)
        {
            // The WinForms list sorts its items when it may select several (Sorting = Ascending).
            items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
        }
        foreach (ISuggestion suggestion in items)
        {
            ListBoxItem container = new ListBoxItem
            {
                Content = suggestion.DisplayName,
                Tag = suggestion,
                Focusable = false,
                FontWeight = suggestion.IsBold ? FontWeights.Bold : FontWeights.Normal
            };
            _allItems.Add(suggestion);
            _containers.Add(container);
            _list.Items.Add(container);
        }
        ApplyColors(0, false);
    }

    public bool IsVisible => _popup.IsOpen;

    public RgbColor BackColor
    {
        set
        {
            _list.Background = new SolidColorBrush(Color.FromArgb(value.A, value.R, value.G, value.B));
        }
    }

    /// <summary>The item that is selected in the list, or null.</summary>
    public ISuggestion SelectedSuggestion => (_list.SelectedItem as ListBoxItem)?.Tag as ISuggestion;

    public void Show(bool autoSize)
    {
        if (autoSize)
        {
            double width = 0;
            double pixelsPerDip = VisualTreeHelper.GetDpi(_host.PlacementTarget).PixelsPerDip;
            foreach (ISuggestion suggestion in _allItems)
            {
                FormattedText text = new FormattedText(suggestion.DisplayName ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(_list.FontFamily, FontStyles.Normal, suggestion.IsBold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
                    _list.FontSize, Brushes.Black, pixelsPerDip);
                width = Math.Max(width, text.WidthIncludingTrailingWhitespace);
            }
            // Room for the padding of an item and the vertical scroll bar.
            _list.Width = Math.Min(width, MaxWidth) + 12 + (_allItems.Count > MaxRows ? SystemParameters.VerticalScrollBarWidth : 0);
            _list.MaxHeight = MaxRows * Math.Ceiling(_list.FontSize * _list.FontFamily.LineSpacing + 4) + 2;
        }
        Rect caret = _host.CaretBounds;
        _popup.PlacementRectangle = new Rect(caret.Left - 16, caret.Top, 16 + caret.Width, caret.Height + 5);
        _popup.IsOpen = true;
        ShowItemHint();
    }

    public void Hide()
    {
        _hintPopup.IsOpen = false;
        _popup.IsOpen = false;
    }

    public void Dispose()
    {
        Hide();
    }

    public void SelectMatch(string prefix, bool exactMatch)
    {
        List<string> names = new List<string>();
        foreach (object item in _list.Items)
        {
            names.Add(((ISuggestion)((ListBoxItem)item).Tag).DisplayName);
        }
        SuggestionMatch match = SuggestionListMatch.Find(names, prefix, exactMatch, StringComparer.CurrentCultureIgnoreCase);
        if (match.Kind == SuggestionMatchKind.Item)
        {
            SelectIndex(match.Index);
            ResetFilter(prefix);
            return;
        }
        if (match.Kind == SuggestionMatchKind.Filtered)
        {
            List<ListBoxItem> top = new List<ListBoxItem>();
            foreach (int index in match.Matches)
            {
                top.Add((ListBoxItem)_list.Items[index]);
            }
            foreach (ListBoxItem item in top)
            {
                _list.Items.Remove(item);
            }
            for (int i = 0; i < top.Count; i++)
            {
                _list.Items.Insert(i, top[i]);
            }
            SelectIndex(0);
            ApplyColors(top.Count, true);
            _isFiltered = true;
        }
        else
        {
            // As the WinForms list: the nearest item was counted in the order the list had, and is selected after the order is put back.
            ResetFilter(prefix);
            SelectIndex(match.Index);
        }
    }

    public void AcceptSelected()
    {
        ISuggestion suggestion = SelectedSuggestion;
        if (_popup.IsOpen && suggestion != null)
        {
            Hide();
            _host.AcceptSuggestion(suggestion, VirtualKey.None);
        }
    }

    /// <summary>
    /// A key the editor got while the list is open. True: the list took it and the editor does nothing more with it; false: the editor handles
    /// it as usual. <paramref name="typeBlank"/> is set when a blank is to be typed into the editor instead (it hid the list first).
    /// </summary>
    public bool HandleKey(VirtualKey key, Func<string> currentText, out bool typeBlank)
    {
        typeBlank = false;
        switch (key)
        {
            case VirtualKey.Up:
                MoveSelection(-1);
                return true;
            case VirtualKey.Down:
                MoveSelection(1);
                return true;
            case VirtualKey.PageUp:
                MoveSelection(-(MaxRows - 1));
                return true;
            case VirtualKey.PageDown:
                MoveSelection(MaxRows - 1);
                return true;
        }
        ISuggestion suggestion = SelectedSuggestion;
        switch (SuggestionListMatch.MapKey(key, suggestion, currentText))
        {
            case SuggestionListKeyAction.Hide:
                Hide();
                return true;
            case SuggestionListKeyAction.HideAndTypeBlank:
                Hide();
                typeBlank = true;
                return true;
            case SuggestionListKeyAction.Accept:
                Hide();
                _host.AcceptSuggestion(suggestion, key);
                return true;
            case SuggestionListKeyAction.ForwardAndSelectFirst:
                SelectIndex(0);
                return false;
            case SuggestionListKeyAction.ForwardAndSelectLast:
                SelectIndex(_list.Items.Count - 1);
                return false;
        }
        return false;
    }

    private void MoveSelection(int delta)
    {
        if (_list.Items.Count == 0)
        {
            return;
        }
        int index = _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex + delta;
        SelectIndex(Math.Max(0, Math.Min(index, _list.Items.Count - 1)));
    }

    private void SelectIndex(int index)
    {
        if (index < 0 || index >= _list.Items.Count)
        {
            _list.SelectedIndex = -1;
            return;
        }
        _list.SelectedIndex = index;
        _list.ScrollIntoView(_list.Items[index]);
    }

    private void ResetFilter(string prefix)
    {
        if (!_isFiltered)
        {
            return;
        }
        _isFiltered = false;
        _list.Items.Clear();
        foreach (ListBoxItem container in _containers)
        {
            _list.Items.Add(container);
        }
        ApplyColors(0, false);
        SelectMatch(prefix, exactMatch: false);
    }

    // The items put on top in the colour of the text, the others grey; without a filter all in the colour of the text.
    private void ApplyColors(int topCount, bool filtered)
    {
        RgbColor fore = _host.ItemForeColor;
        Brush text = new SolidColorBrush(Color.FromArgb(fore.A, fore.R, fore.G, fore.B));
        Brush grey = new SolidColorBrush(Color.FromArgb(fore.A, fore.R, fore.G, fore.B)) { Opacity = 0.45 };
        for (int i = 0; i < _list.Items.Count; i++)
        {
            ((ListBoxItem)_list.Items[i]).Foreground = (!filtered || i < topCount) ? text : grey;
        }
    }

    // The signature of the selected item beside the list, as the WinForms list shows it; none for an item without one.
    private void ShowItemHint()
    {
        ISuggestion suggestion = SelectedSuggestion;
        string hint = suggestion?.HintText;
        if (!_popup.IsOpen || string.IsNullOrEmpty(hint))
        {
            _hintPopup.IsOpen = false;
            return;
        }
        _hintText.Text = hint;
        _hintDescription.Text = suggestion.HintDescription ?? "";
        _hintDescription.Visibility = string.IsNullOrEmpty(suggestion.HintDescription) ? Visibility.Collapsed : Visibility.Visible;
        _hintPopup.IsOpen = false;
        _hintPopup.IsOpen = true;
    }
}
