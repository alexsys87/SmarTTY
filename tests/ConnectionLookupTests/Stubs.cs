using System;
using System.IO;

// The settings classes use the enums of System.IO.Ports, which modern .NET ships as a separate package. The
// application targets .NET Framework where they are built in; this harness only needs the names.
namespace System.IO.Ports
{
    public enum Parity { None, Odd, Even, Mark, Space }

    public enum StopBits { None, One, Two, OnePointFive }

    public enum Handshake { None, XOnXOff, RequestToSend, RequestToSendXOnXOff }
}

namespace VisualTTY
{
    /// <summary>
    /// Stand-in for the application settings: the store only asks it for the default settings directory, which the
    /// tests never use because they pass a directory of their own.
    /// </summary>
    public static class GlobalSettings
    {
        public static string DataDirectory => Path.Combine(Path.GetTempPath(), "SmarTTY-ConnectionLookupTests");
    }
}
