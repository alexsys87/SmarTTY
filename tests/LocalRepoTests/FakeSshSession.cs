using System;
using System.Collections.Generic;

namespace SmarTTY.Ssh
{
    /// <summary>
    /// A stand-in for SmarTTY.Ssh.SshSession (which needs SSH.NET and the Windows build) with the three members that the local
    /// repository host uses. It records the commands and the tunnel requests, and can fail on demand.
    /// </summary>
    public class SshSession
    {
        public readonly List<string> Commands = new List<string>();

        public bool IsRootUser = true;

        public bool OpkgMissing;

        public bool FailEnable;

        public bool FailTunnel;

        public uint? TunnelPort;

        public int TunnelStarts;

        public int TunnelStops;

        /// <summary>When set, the feed scripts are run by a real shell with the configuration file path replaced by this one.</summary>
        public string LocalConfigFile;

        public Func<string, string> RunShell;

        public List<string> RunCommandChecked(string command, bool requireSuccess, string terminalType = null)
        {
            lock (Commands)
            {
                Commands.Add(command);
            }
            if (command == "id -u")
            {
                return new List<string> { IsRootUser ? "0" : "1000" };
            }
            if (command == "command -v opkg >/dev/null 2>&1")
            {
                return OpkgMissing ? null : new List<string>();
            }
            if (command.Contains("smartty-local-begin"))
            {
                if (FailEnable && command.Contains("printf"))
                {
                    return null;
                }
                if (command.StartsWith("sudo -n sh -c ") || LocalConfigFile == null)
                {
                    return new List<string>();
                }
                string output = RunShell(command.Replace("/etc/opkg/customfeeds.conf", LocalConfigFile));
                return output == null ? null : new List<string>();
            }
            return new List<string>();
        }

        public void StartRemoteForward(uint boundPort, string host, uint hostPort)
        {
            if (FailTunnel)
            {
                throw new InvalidOperationException("administratively prohibited");
            }
            if (host != "127.0.0.1" || boundPort != hostPort)
            {
                throw new InvalidOperationException("unexpected tunnel " + boundPort + " -> " + host + ":" + hostPort);
            }
            TunnelPort = boundPort;
            TunnelStarts++;
        }

        public void StopRemoteForward()
        {
            if (TunnelPort != null)
            {
                TunnelStops++;
                TunnelPort = null;
            }
        }
    }
}
