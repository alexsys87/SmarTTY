using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SmarTTY.Forms;

/// <summary>
/// Window icons of the WinForms dialogs (icons/forms/*.ico, embedded as "SmarTTY.FormIcons.&lt;name&gt;.ico").
/// Without one a form shows the generic WinForms icon in its title bar, on the taskbar and in Alt+Tab.
/// </summary>
internal static class FormIcons
{
    private static readonly Dictionary<string, Icon> _cache = new Dictionary<string, Icon>();

    /// <summary>The application icon, used by every DpiAwareForm that does not choose its own.</summary>
    public static Icon Application => Load("app");

    /// <summary>Gives the form the named icon and shows it in the title bar.</summary>
    public static void Apply(Form form, string name)
    {
        Icon icon = Load(name);
        if (icon != null)
        {
            form.Icon = icon;
            form.ShowIcon = true;
        }
    }

    public static Icon Load(string name)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(name, out Icon icon))
            {
                using (Stream stream = typeof(FormIcons).Assembly.GetManifestResourceStream("SmarTTY.FormIcons." + name + ".ico"))
                {
                    // The icon keeps every size of the file: Windows picks the one for the title bar and the taskbar.
                    icon = stream != null ? new Icon(stream) : null;
                }
                _cache[name] = icon;
            }
            return icon;
        }
    }
}
