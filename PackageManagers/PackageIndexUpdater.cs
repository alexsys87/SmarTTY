using System;
using System.Collections.Generic;
using System.Linq;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// Refreshes the package lists on the remote host with the detected package manager ("apt-get update", "opkg update", ...).
/// </summary>
public static class PackageIndexUpdater
{
    internal const string ExitMarker = "__SMARTTY_EXIT__=";

    private const int ErrorTailLines = 8;

    /// <summary>The longest time the update of the package lists may take before it is abandoned.</summary>
    internal static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Tells whether the SSH user on the remote host is root. Devices such as OpenWrt usually have no sudo at all,
    /// so privilege escalation is used only when this returns false.
    /// </summary>
    public static bool IsRoot(ISSHCommandRunner runner)
    {
        try
        {
            string[] output = runner.RunCommand("id -u");
            return output != null && output.Any(line => line.Trim() == "0");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wraps a command line that is typed into the terminal: as is for root, through "sudo su -c" for other users.
    /// </summary>
    public static string WrapForPrivileges(string commandLine, bool isRoot)
    {
        return isRoot ? commandLine : "sudo su -c \"" + commandLine + "\"";
    }

    /// <summary>
    /// Wraps <paramref name="updateCommand"/> so that it runs as is for root (no sudo involved) or through non-interactive
    /// sudo for other users, and so that its exit status is printed on the last line (the runner reports only the output
    /// of a failed command as null).
    /// </summary>
    public static string BuildCommandLine(string updateCommand, bool isRoot)
    {
        if (string.IsNullOrWhiteSpace(updateCommand))
        {
            throw new ArgumentException("The update command is empty", nameof(updateCommand));
        }
        string command = isRoot ? updateCommand : "sudo -n " + updateCommand;
        return "{ " + command + "; } 2>&1; echo " + ExitMarker + "$?";
    }

    /// <summary>Runs the update on the remote host; throws with the tail of the command output if it fails.</summary>
    public static void Run(ISSHCommandRunner runner, ILinuxPackageManagerClient client, bool isRoot)
    {
        string command = client.GetCommandLineForIndexUpdate();
        if (client is WingetPackageManagerClient)
        {
            // The shell of a Windows host is not sh: no "{ ...; } 2>&1; echo", no sudo. The runner reports a failed command as null.
            string[] wingetOutput = runner is ISSHCommandRunnerWithTimeout wingetRunner
                ? wingetRunner.RunCommand(command, UpdateTimeout)
                : runner.RunCommand(command);
            if (wingetOutput == null)
            {
                throw new Exception("Failed to run '" + command + "' on the remote host.");
            }
            return;
        }
        string commandLine = BuildCommandLine(command, isRoot);
        // Without internet access the package manager can retry connections for a very long time and the program
        // would wait for it (holding the connection busy), so give up after a while.
        string[] lines = runner is ISSHCommandRunnerWithTimeout timedRunner
            ? timedRunner.RunCommand(commandLine, UpdateTimeout)
            : runner.RunCommand(commandLine);
        if (lines == null && runner is ISSHCommandRunnerWithTimeout)
        {
            throw new Exception("Failed to update the package lists ('" + command + "'): no result within " + (int)UpdateTimeout.TotalSeconds
                + " seconds or the command could not be run. Check that the remote host has internet access.");
        }
        CheckResult(lines, command, isRoot);
    }

    internal static void CheckResult(string[] lines, string command, bool isRoot)
    {
        if (lines == null)
        {
            throw new Exception("Failed to run '" + command + "' on the remote host.");
        }
        int? exitCode = null;
        List<string> output = new List<string>();
        foreach (string line in lines)
        {
            // A command whose last output has no newline (a progress line) leaves the marker glued to that text.
            int markerIndex = line.IndexOf(ExitMarker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                if (int.TryParse(line.Substring(markerIndex + ExitMarker.Length).Trim(), out int parsed))
                {
                    exitCode = parsed;
                }
                if (markerIndex > 0 && !string.IsNullOrWhiteSpace(line.Substring(0, markerIndex)))
                {
                    output.Add(line.Substring(0, markerIndex).TrimEnd());
                }
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                output.Add(line.TrimEnd());
            }
        }
        if (exitCode == 0)
        {
            return;
        }
        string tail = string.Join("\n", output.Skip(Math.Max(0, output.Count - ErrorTailLines)).ToArray());
        string message = "Failed to update the package lists ('" + command + "'" + (exitCode.HasValue ? ", exit code " + exitCode.Value : "") + ").";
        if (tail.Length != 0)
        {
            message += "\n\n" + tail;
        }
        if (!isRoot)
        {
            message += "\n\nUpdating the package lists needs root rights: log in as root or allow passwordless sudo for this user.";
        }
        throw new Exception(message);
    }
}
