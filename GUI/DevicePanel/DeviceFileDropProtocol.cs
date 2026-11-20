namespace SmarTTY.GUI.DevicePanel;

/// <summary>How a file dropped on a COM-port / TCP terminal is sent to the device.</summary>
public enum DeviceFileDropProtocol
{
    XModem,
    XModem1K,
    YModem,
    ZModem,
    Kermit,
    /// <summary>Through the shell of a Linux device: packed with gzip, typed as base64, unpacked there (one file).</summary>
    GzipBase64,
    /// <summary>The bytes of the file are written to the line as they are, with no protocol.</summary>
    RawSend,
    /// <summary>Through the shell of a Linux device: packed with gzip and sent as it is, with no text form, read there in raw mode (one file).</summary>
    GzipCat,
}
