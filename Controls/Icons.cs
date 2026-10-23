using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// Images of the WinForms controls: menu items, context menus, picture boxes and what the editors draw (icons/**/*.png, the
/// ones that code needs are embedded as "SmarTTY.Icons.&lt;name&gt;.png", see SmarTTY.csproj). The images are shared and never disposed.
/// </summary>
internal static class Icons
{
    private static readonly Dictionary<string, Image> _cache = new Dictionary<string, Image>();

    /// <summary>Gives the menu item (or toolbar button) the named image; an item whose image is missing simply stays without one.</summary>
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
                using (Stream stream = typeof(Icons).Assembly.GetManifestResourceStream("SmarTTY.Icons." + name + ".png"))
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
