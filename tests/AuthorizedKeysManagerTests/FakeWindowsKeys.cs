using System;
using SmarTTY.Ssh;

namespace SmarTTY
{
    /// <summary>
    /// A stand-in for WindowsAuthorizedKeys.cs, which works over SFTP: the fake SshSession here is never a Windows host, and
    /// the real code is run against a live server by tests/SshNetIntegrationTests.
    /// </summary>
    internal static class WindowsAuthorizedKeys
    {
        public static bool IsKeyAuthorized(SshSession session, byte[] keyBlob)
        {
            throw new NotSupportedException();
        }

        public static void Append(SshSession session, string keyLine, byte[] keyBlob)
        {
            throw new NotSupportedException();
        }

        public static void Remove(SshSession session, byte[] keyBlob)
        {
            throw new NotSupportedException();
        }
    }
}
