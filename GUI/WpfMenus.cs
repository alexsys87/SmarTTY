using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmarTTY.GUI;

/// <summary>
/// Pieces of the WPF menus that replace the WinForms ones (stage 4): the images of the items, from the same embedded files the WinForms
/// menus used (<c>SmarTTY.Icons.&lt;name&gt;.png</c>, see <c>Controls/Icons</c>), items made in one call, and a menu opened at the mouse.
/// The look is the theme's: <c>GUI/Styles/Controls.xaml</c> styles every <see cref="ContextMenu"/> and <see cref="MenuItem"/>.
/// </summary>
internal static class WpfMenus
{
    private static readonly Dictionary<string, ImageSource> _icons = new Dictionary<string, ImageSource>();

    /// <summary>The image of a name (as <c>Icons.Load</c> knows it), or null when there is none. Frozen and shared.</summary>
    public static ImageSource LoadIcon(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        lock (_icons)
        {
            if (!_icons.TryGetValue(name, out ImageSource image))
            {
                using (Stream stream = typeof(WpfMenus).Assembly.GetManifestResourceStream("SmarTTY.Icons." + name + ".png"))
                {
                    if (stream != null)
                    {
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        image = bitmap;
                    }
                }
                _icons[name] = image;
            }
            return image;
        }
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
