using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace SmarTTY.Properties;

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
                resourceMan = new ResourceManager("SmarTTY.Properties.Resources", typeof(Resources).Assembly);
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

    public static Bitmap add16 => (Bitmap)ResourceManager.GetObject("add16", resourceCulture);

    public static Bitmap CopyHS => (Bitmap)ResourceManager.GetObject("CopyHS", resourceCulture);

    public static Bitmap delete16 => (Bitmap)ResourceManager.GetObject("delete16", resourceCulture);

    public static Bitmap new16 => (Bitmap)ResourceManager.GetObject("new16", resourceCulture);

    public static Bitmap PasteHS => (Bitmap)ResourceManager.GetObject("PasteHS", resourceCulture);

    internal Resources()
    {
    }
}
