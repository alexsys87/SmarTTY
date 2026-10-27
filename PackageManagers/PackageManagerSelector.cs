using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Picks the package manager client of a host from the scores of the clients' probes (no SSH of its own, so it is tested by
/// tests/ApkPackageManagerTests). The client with the highest score wins and the first one among equals; a score of
/// <see cref="MaxScore"/> cannot be beaten, so the probes after it are not run at all: each probe is a round trip over
/// SSH (a command that has to start on a possibly slow device), and eight of them in a row were run even on a host
/// that answered to the first one.
/// </summary>
public static class PackageManagerSelector
{
    /// <summary>The highest score a probe can return.</summary>
    public const int MaxScore = 1000;

    /// <summary>
    /// The client with the best positive score, or null when none of the probes found its package manager or
    /// <paramref name="isCancelled"/> says so (the window was closed while probing).
    /// </summary>
    public static ILinuxPackageManagerClient Pick(IEnumerable<ILinuxPackageManagerClient> clients, ISSHCommandRunner runner, Func<bool> isCancelled = null)
    {
        ILinuxPackageManagerClient best = null;
        int bestScore = 0;
        foreach (ILinuxPackageManagerClient client in clients)
        {
            if (isCancelled != null && isCancelled())
            {
                return null;
            }
            int score = -1;
            try
            {
                score = client.Probe(runner);
            }
            catch
            {
                // A probe that fails does not decide anything; the caller tells a lost connection from "no package manager".
            }
            if (score > bestScore)
            {
                best = client;
                bestScore = score;
            }
            if (bestScore >= MaxScore)
            {
                break;
            }
        }
        return best;
    }

    /// <summary>
    /// Tells what the host is: the kernel and the architecture, then the name of the distribution. The error output of a
    /// missing command is dropped, and the last command always succeeds, so that the runner returns the lines.
    /// </summary>
    public const string HostInfoCommand = "uname -sm 2>/dev/null; grep -s '^PRETTY_NAME=' /etc/os-release; true";

    private static readonly Regex KernelLineRegex = new Regex(@"^[A-Za-z][A-Za-z0-9_.\-]*[ \t]+[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);

    /// <summary>"Linux armv7l, OpenWrt 23.05.5" from the output of <see cref="HostInfoCommand"/>; empty when nothing is known.</summary>
    public static string DescribeHost(string[] lines)
    {
        string kernel = null;
        string distribution = null;
        foreach (string rawLine in lines ?? new string[0])
        {
            string line = rawLine?.Trim() ?? "";
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                distribution = line.Substring("PRETTY_NAME=".Length).Trim().Trim('"', '\'');
            }
            else if (kernel == null && KernelLineRegex.IsMatch(line))
            {
                kernel = line;
            }
        }
        return string.Join(", ", new[] { kernel, distribution }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>
    /// The message for a host where no probe found a package manager: what the host is and which managers are supported, so
    /// that a missing one (Void's xbps, FreeBSD's pkg) is told from a probe that failed.
    /// </summary>
    public static string BuildNotFoundMessage(ISSHCommandRunner runner, IEnumerable<ILinuxPackageManagerClient> clients)
    {
        string host = "";
        try
        {
            host = DescribeHost(runner.RunCommand(HostInfoCommand));
        }
        catch
        {
            // The description is a courtesy: the error is shown without it.
        }
        string supported = string.Join(", ", clients.Select(client => client.GetType().Name.Replace("PackageManagerClient", "").ToLowerInvariant()));
        return "Could not detect the package manager on the remote host" + (host.Length == 0 ? "" : " (" + host + ")") + ". Supported: " + supported + ".";
    }
}
