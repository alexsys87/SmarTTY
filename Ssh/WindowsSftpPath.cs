using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SmarTTY.Ssh;

/// <summary>
/// Paths of a Windows SSH server as SFTP shows them: with forward slashes and the drive as the first segment ("/C:/Users/me",
/// what Win32-OpenSSH answers to realpath "."). "/" is above the drives. A drive root is always written with its slash
/// ("/C:/"), because "/C:" is the current directory of the drive C: and not its root. Nothing here talks to the server, so the
/// rules are checked by tests/WindowsHostTests.
/// </summary>
public static class WindowsSftpPath
{
    private static readonly Regex DriveSegment = new Regex("^[A-Za-z]:$", RegexOptions.Compiled);

    private static readonly Regex DriveRootPath = new Regex("^/[A-Za-z]:/$", RegexOptions.Compiled);

    /// <summary>
    /// The canonical form of an absolute path: forward slashes, a leading slash ("C:\Users" becomes "/C:/Users"), no "." and
    /// "..", no doubled or trailing slashes (except the one of a drive root), an upper case drive letter.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }
        string text = path.Replace('\\', '/');
        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':' && (text.Length == 2 || text[2] == '/'))
        {
            text = "/" + text;
        }
        List<string> segments = new List<string>();
        foreach (string segment in text.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            segments.Add(segment);
        }
        if (segments.Count == 0)
        {
            return "/";
        }
        if (DriveSegment.IsMatch(segments[0]))
        {
            segments[0] = char.ToUpperInvariant(segments[0][0]) + ":";
            if (segments.Count == 1)
            {
                return "/" + segments[0] + "/";
            }
        }
        return "/" + string.Join("/", segments);
    }

    /// <summary>
    /// <paramref name="path"/> as seen from <paramref name="directory"/>: an absolute path ("/C:/x", "C:\x", "\x") stands for
    /// itself, a relative one ("Users", "..") is looked up in the directory.
    /// </summary>
    public static string Combine(string directory, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Normalize(directory);
        }
        string text = path.Replace('\\', '/');
        bool absolute = text[0] == '/' || (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':');
        return Normalize(absolute ? text : (directory ?? "/").TrimEnd('/') + "/" + text);
    }

    /// <summary>The directory above: "/C:/Users" -> "/C:/", the root of a drive -> "/", "/" stays.</summary>
    public static string Parent(string path)
    {
        return Combine(path, "..");
    }

    /// <summary>True for "/C:" and "/C:/".</summary>
    public static bool IsDriveRoot(string path)
    {
        return DriveRootPath.IsMatch(Normalize(path));
    }

    /// <summary>True for "/", the place above the drives.</summary>
    public static bool IsRoot(string path)
    {
        return Normalize(path) == "/";
    }

    /// <summary>The path as the Windows tools write it: "/C:/Users/me" -> "C:\Users\me", "/C:/" -> "C:\"; null for a path without a drive.</summary>
    public static string ToWindows(string path)
    {
        string normalized = Normalize(path);
        if (normalized.Length < 3 || !DriveSegment.IsMatch(normalized.Substring(1, 2)))
        {
            return null;
        }
        return normalized.Substring(1).Replace('/', '\\');
    }

    /// <summary>
    /// The path as it is sent to the server: a drive root with its slash (a bare "/C:" would mean the current directory of the
    /// drive), anything else as it is.
    /// </summary>
    public static string ForServer(string path)
    {
        if (path != null && Regex.IsMatch(path, "^/[A-Za-z]:$"))
        {
            return path + "/";
        }
        return path;
    }
}
