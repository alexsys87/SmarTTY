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

    /// <summary>
    /// Stand-in for the key container the settings name. The real class also builds RSA keys, which this harness cannot:
    /// the lookup tests only need the name to compile.
    /// </summary>
    public static class AppEnvironment
    {
        public const string PasswordContainerName = "com.sysprogs.smartty.passwords";
    }

    /// <summary>
    /// Stand-in for the password encryption, which needs the Windows key container and is not available here. The lookup
    /// tests never call it; it throws rather than silently returning something wrong.
    /// </summary>
    public static class PasswordProtector
    {
        public static byte[] Encrypt(string password, string containerName) => throw new NotSupportedException("The key container is not available in this harness.");

        public static string Decrypt(byte[] encryptedBytes, string containerName) => throw new NotSupportedException("The key container is not available in this harness.");
    }
}
