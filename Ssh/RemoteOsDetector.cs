using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY.Ssh;

/// <summary>What the SSH server runs on, as far as SmarTTY can tell.</summary>
public enum RemoteOsKind
{
    /// <summary>Nothing says otherwise: the host is treated like the Linux and BSD hosts SmarTTY is written for.</summary>
    Unknown,

    /// <summary>The server announced itself as a Unix one (Dropbear, a Debian or Ubuntu build of OpenSSH, FreeBSD).</summary>
    Unix,

    /// <summary>A Windows host (Win32-OpenSSH, Bitvise): no df, ls, sh or package manager of Linux there.</summary>
    Windows
}

/// <summary>
/// Tells a Windows SSH server from the Unix ones. The probes and the file listing of SmarTTY are shell commands of Linux and
/// BSD hosts; on Windows they fail one after the other (the disk bar polling df every ten seconds, the package manager window
/// probing eight package managers), each time with the output of cmd.exe instead of an answer.
/// The first source is the identification string of the server, which the SSH handshake delivers anyway: Win32-OpenSSH,
/// the OpenSSH of Windows 10, 11 and Server 2019+, calls itself "OpenSSH_for_Windows_9.5". The second one is a command for
/// the hosts whose banner says nothing and that could not answer the Unix probe either.
/// </summary>
public static class RemoteOsDetector
{
    // Win32-OpenSSH, Bitvise SSH Server ("FlowSsh: Bitvise SSH Server (WinSSHD)"), freeSSHd ("WeOnlyDo").
    private static readonly string[] WindowsServerMarkers = { "OpenSSH_for_Windows", "Bitvise", "WinSSHD", "WeOnlyDo" };

    private static readonly string[] UnixServerMarkers = { "dropbear", "Ubuntu", "Debian", "Raspbian", "FreeBSD" };

    /// <summary>
    /// Prints "Windows_NT" on a Windows host whatever its default shell is: cmd.exe expands %OS% and takes $env:OS as text,
    /// PowerShell takes %OS% as text and expands $env:OS. A POSIX shell expands neither (it prints "%OS% :OS"), and tcsh
    /// answers "Undefined variable", so no Unix host ever prints it. Sent as it is, not through sh: sh is what Windows lacks.
    /// </summary>
    public const string WindowsProbeCommand = "echo %OS% $env:OS";

    /// <summary>The kind of host from the identification string of the SSH server ("SSH-2.0-OpenSSH_for_Windows_9.5").</summary>
    public static RemoteOsKind FromServerVersion(string serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            return RemoteOsKind.Unknown;
        }
        if (WindowsServerMarkers.Any(marker => serverVersion.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return RemoteOsKind.Windows;
        }
        if (UnixServerMarkers.Any(marker => serverVersion.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return RemoteOsKind.Unix;
        }
        return RemoteOsKind.Unknown;
    }

    /// <summary>Windows when the output of <see cref="WindowsProbeCommand"/> holds the word Windows_NT, otherwise unknown.</summary>
    public static RemoteOsKind FromWindowsProbe(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return RemoteOsKind.Unknown;
        }
        foreach (string line in lines)
        {
            if (line != null && line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Contains("Windows_NT", StringComparer.Ordinal))
            {
                return RemoteOsKind.Windows;
            }
        }
        return RemoteOsKind.Unknown;
    }
}
