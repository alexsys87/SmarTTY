using System;
using System.Collections.Generic;
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
}
