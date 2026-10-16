using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace VisualTTY.Properties;

[GeneratedCode("System.Resources.Tools.StronglyTypedResourceBuilder", "15.0.0.0")]
[DebuggerNonUserCode]
[CompilerGenerated]
internal class Resources
{
    private static ResourceManager resourceMan;

    private static CultureInfo resourceCulture;

    [EditorBrowsable(EditorBrowsableState.Advanced)]
    internal static ResourceManager ResourceManager
    {
        get
        {
            if (resourceMan == null)
            {
                resourceMan = new ResourceManager("VisualTTY.Properties.Resources", typeof(Resources).Assembly);
            }
            return resourceMan;
        }
    }

    [EditorBrowsable(EditorBrowsableState.Advanced)]
    internal static CultureInfo Culture
    {
        get
        {
            return resourceCulture;
        }
        set
        {
            resourceCulture = value;
        }
    }

    internal static Bitmap DocumentHS => (Bitmap)ResourceManager.GetObject("DocumentHS", resourceCulture);

    internal static Bitmap event16 => (Bitmap)ResourceManager.GetObject("event16", resourceCulture);

    internal static Bitmap locked => (Bitmap)ResourceManager.GetObject("locked", resourceCulture);

    internal static Bitmap unlocked => (Bitmap)ResourceManager.GetObject("unlocked", resourceCulture);

    internal static Bitmap unlocked32 => (Bitmap)ResourceManager.GetObject("unlocked32", resourceCulture);

    internal Resources()
    {
    }
}
