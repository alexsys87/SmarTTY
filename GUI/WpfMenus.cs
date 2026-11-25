using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SmarTTY.GUI;

/// <summary>
/// Pieces of the WPF menus that replace the WinForms ones (stage 4): the images of the items by the names the WinForms menus used
/// (vector images since stage 5A, see <see cref="IconImages"/>), items made in one call, and a menu opened at the mouse.
/// The look is the theme's: <c>GUI/Styles/Controls.xaml</c> styles every <see cref="ContextMenu"/> and <see cref="MenuItem"/>.
/// </summary>
internal static class WpfMenus
{
    // The names the menus ask for and the icons they are (the names the WinForms menus embedded the PNG files under; the
    // editor's icons are asked for by their own names, "editor/<name>").
    private static readonly Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "upload", "MainWindow/upload" },
        { "download", "MainWindow/download" },
        { "terminal", "term16" },
        { "settings", "gear16" },
        { "edit", "edit16" },
        { "delete", "delete16" },
        { "network", "net16" },
        { "refresh", "MainWindow/refresh16" },
        { "question", "question16" },
        { "go", "MainWindow/go16" },
        { "event", "event16" },
        { "copy", "MainWindow/copy" },
        { "paste", "MainWindow/paste" },
        { "add", "MainWindow/add16" },
        { "clear", "transfers/removeall" },
        { "save", "MainWindow/save16" },
        { "cut", "menu/cut" },
        { "find", "MainWindow/FindHS" },
        { "key-locked", "keys/locked" },
        { "key-unlocked", "keys/unlocked" },
        { "terminal-disconnect", "terminal-disconnect" },
        { "terminal-reconnect", "terminal-reconnect" },
        { "terminal-send", "terminal-send" },
        { "terminal-clear", "terminal-clear" },
        { "terminal-transfer", "terminal-transfer" },
        { "terminal-save", "terminal-save" },
        { "terminal-settings", "terminal-settings" },
        { "busy", "MainWindow/busy" }
    };

    /// <summary>The icon path (see <see cref="IconImages"/>) of a name the menus ask for, or null for an unknown name.</summary>
    internal static string PathOf(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        if (_names.TryGetValue(name, out string path))
        {
            return path;
        }
        // The rest are the editor's icons by their names.
        return "editor/" + name;
    }

    /// <summary>The image of a name the menus ask for, or null when there is none. Frozen and shared.</summary>
    public static ImageSource LoadIcon(string name)
    {
        string path = PathOf(name);
        return path != null ? IconImages.Get(path) : null;
    }

    /// <summary>The icon of a menu item or a tool bar button: the image at 16 x 16, or null.</summary>
    public static Image Icon(string name)
    {
        ImageSource source = LoadIcon(name);
        if (source == null)
        {
            return null;
        }
        Image image = new Image { Source = source, Width = 16, Height = 16 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>
    /// A text as a header: an underscore is a letter, not the mark of an access key (the texts of the user's commands and file names have
    /// them; the WinForms menus doubled the ampersand for the same reason).
    /// </summary>
    public static string PlainText(string text)
    {
        return (text ?? "").Replace("_", "__");
    }

    /// <summary>An item with its text, image, action and tool tip.</summary>
    public static MenuItem Item(string header, string icon, Action click, string toolTip = null)
    {
        MenuItem item = new MenuItem { Header = header, Icon = Icon(icon) };
        if (toolTip != null)
        {
            item.ToolTip = toolTip;
            ToolTipService.SetShowOnDisabled(item, true);
        }
        if (click != null)
        {
            item.Click += (sender, e) => click();
        }
        return item;
    }

    /// <summary>An item with a check mark that changes it when it is clicked (the WinForms items with CheckOnClick).</summary>
    public static MenuItem CheckItem(string header, bool isChecked, Action<bool> changed, string toolTip = null)
    {
        MenuItem item = Item(header, null, null, toolTip);
        item.IsCheckable = true;
        item.IsChecked = isChecked;
        item.Click += (sender, e) => changed?.Invoke(item.IsChecked);
        return item;
    }

    /// <summary>Opens a menu at the mouse over <paramref name="target"/> (a right click).</summary>
    public static void OpenAtMouse(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Opens a menu below an element (the drop-down of a tool bar button).</summary>
    public static void OpenBelow(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}

/// <summary>A menu item made in one expression, with an object initializer after it (the way the WinForms menu items of the device panel were made).</summary>
internal class DeviceMenuItem : MenuItem
{
    public DeviceMenuItem(string text, string icon = null, RoutedEventHandler click = null)
    {
        Header = text;
        Icon = WpfMenus.Icon(icon);
        if (click != null)
        {
            Click += click;
        }
    }

    public DeviceMenuItem()
    {
    }
}

/// <summary>What the WinForms <c>Visible</c> of a control was, for a WPF element.</summary>
internal static class VisibilityExtensions
{
    public static void SetVisible(this UIElement element, bool visible)
    {
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public static bool IsShown(this UIElement element)
    {
        return element.Visibility == Visibility.Visible;
    }
}
