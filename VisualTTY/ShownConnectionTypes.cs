
using System.ComponentModel;
using System.Xml.Serialization;

namespace VisualTTY;

public struct ShownConnectionTypes
{
    public bool SSH;

    public bool RawTcp;

    /// <summary>
    /// Reads the pre-rename <c>&lt;Telnet&gt;</c> element of GUIPreferences.xml so existing
    /// settings keep working. Never written back.
    /// </summary>
    [XmlElement("Telnet")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool LegacyTelnet
    {
        get => RawTcp;
        set => RawTcp = value;
    }

    public bool ShouldSerializeLegacyTelnet() => false;

    public bool Serial;
}
