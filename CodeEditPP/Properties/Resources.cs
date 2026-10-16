using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace CodeEditPP.Properties;

[GeneratedCode("System.Resources.Tools.StronglyTypedResourceBuilder", "4.0.0.0")]
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
                resourceMan = new ResourceManager("CodeEditPP.Properties.Resources", typeof(Resources).Assembly);
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

    internal static Bitmap down => (Bitmap)ResourceManager.GetObject("down", resourceCulture);

    internal static Bitmap error => (Bitmap)ResourceManager.GetObject("error", resourceCulture);

    internal static Bitmap fastDown => (Bitmap)ResourceManager.GetObject("fastDown", resourceCulture);

    internal static Bitmap fastUp => (Bitmap)ResourceManager.GetObject("fastUp", resourceCulture);

    internal static Bitmap up => (Bitmap)ResourceManager.GetObject("up", resourceCulture);

    internal static Bitmap warning => (Bitmap)ResourceManager.GetObject("warning", resourceCulture);

    internal Resources()
    {
    }
}
