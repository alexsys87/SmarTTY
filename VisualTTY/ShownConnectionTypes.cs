
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

    /// <summary>
    /// Telnet connections are hidden when this is set. It is stored inverted on purpose: GUIPreferences.xml files
    /// written before telnet existed have no such element, and for those telnet connections must be shown
    /// (a "show" flag would read as false and hide them).
    /// </summary>
    public bool HideTelnet;
}
