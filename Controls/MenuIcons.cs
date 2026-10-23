using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// Images of the menu items of the WinForms controls (icons/*.png, embedded as "SmarTTY.MenuIcons.&lt;name&gt;.png").
/// The images are shared by all menus and are never disposed.
/// </summary>
internal static class MenuIcons
{
    private static readonly Dictionary<string, Image> _cache = new Dictionary<string, Image>();

    /// <summary>Gives the menu item the named image; an item whose image is missing simply stays without one.</summary>
    public static void Apply(ToolStripItem item, string name)
    {
        Image image = Load(name);
        if (image != null)
        {
            item.Image = image;
        }
    }

    public static Image Load(string name)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(name, out Image image))
            {
                using (Stream stream = typeof(MenuIcons).Assembly.GetManifestResourceStream("SmarTTY.MenuIcons." + name + ".png"))
                {
                    // The stream has to stay open for the life of a Bitmap, so the image is copied out of it.
                    image = stream != null ? new Bitmap(new Bitmap(stream)) : null;
                }
                _cache[name] = image;
            }
            return image;
        }
    }
}
