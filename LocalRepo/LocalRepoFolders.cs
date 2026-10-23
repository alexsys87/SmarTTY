using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmarTTY.LocalRepo;

/// <summary>
/// The folders of the local repository as the user types them: cleaning up a pasted path, the two spellings of a WSL path,
/// and a description of how far a path that was not found does exist. The checks of the file system are passed in, so that
/// the logic does not depend on Windows (or on a distribution that is running).
/// </summary>
public static class LocalRepoFolders
{
    private const string WslLocalhost = "\\\\wsl.localhost\\";

    private const string WslDollar = "\\\\wsl$\\";

    private const int MaxListed = 12;

    /// <summary>
    /// The path as it is meant: blanks and quotes around it (Explorer's "Copy as path" adds them) and trailing separators are
    /// taken away, %VARIABLES% are expanded, and a path that starts with two slashes is a network path with backslashes.
    /// A root such as "C:\" or "\\server\share" keeps its separator.
    /// </summary>
    public static string Clean(string text)
    {
        string path = (text ?? "").Trim().Trim('"').Trim();
        if (path.Length == 0)
        {
            return "";
        }
        path = Environment.ExpandEnvironmentVariables(path);
        if (path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal))
        {
            path = path.Replace('/', '\\');
        }
        bool network = path.StartsWith("\\\\", StringComparison.Ordinal);
        string trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0)
        {
            return path;
        }
        // "C:\" is the root of a drive, not "C:"; "\\server\share" is as short as a network path gets.
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return trimmed + "\\";
        }
        if (network && trimmed.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            return path;
        }
        return trimmed;
    }

    /// <summary>The path itself and, for a WSL path, the other spelling of it: Windows 10 before 21H2 knows only \\wsl$\.</summary>
    public static IEnumerable<string> Candidates(string path)
    {
        yield return path;
        if (path.StartsWith(WslLocalhost, StringComparison.OrdinalIgnoreCase))
        {
            yield return WslDollar + path.Substring(WslLocalhost.Length);
        }
        else if (path.StartsWith(WslDollar, StringComparison.OrdinalIgnoreCase))
        {
            yield return WslLocalhost + path.Substring(WslDollar.Length);
        }
    }

    /// <summary>The first spelling of the path that exists, or null.</summary>
    public static string FindExisting(string path, Func<string, bool> exists)
    {
        return Candidates(path).FirstOrDefault(exists);
    }

    public static bool IsWslPath(string path)
    {
        return path.StartsWith(WslLocalhost, StringComparison.OrdinalIgnoreCase) || path.StartsWith(WslDollar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tells why <paramref name="path"/> was not found: the part of it that does exist, what is missing next to it and what the
    /// folder there holds (a misspelled name shows up at once). <paramref name="children"/> lists the names in a folder.
    /// </summary>
    public static string DescribeMissing(string path, Func<string, bool> exists, Func<string, IList<string>> children)
    {
        string[] parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        bool network = path.StartsWith("\\\\", StringComparison.Ordinal);
        // A network path is reachable at the earliest as server\share; a local one as a drive (or from the first folder).
        int smallest = network ? 2 : 1;
        string joiner = "\\";
        string Build(int count)
        {
            string head = string.Join(joiner, parts.Take(count).ToArray());
            return network ? "\\\\" + head : (path.StartsWith("/", StringComparison.Ordinal) ? "/" + head.Replace('\\', '/') : head);
        }

        StringBuilder text = new StringBuilder("The folder was not found: " + path);
        int found = 0;
        for (int count = parts.Length - 1; count >= smallest; count--)
        {
            if (exists(Build(count)))
            {
                found = count;
                break;
            }
        }
        if (found > 0)
        {
            string existing = Build(found);
            text.Append("\n\nThe path exists up to ").Append(existing).Append(", but there is no \"").Append(parts[found]).Append("\" in it.");
            IList<string> names;
            try
            {
                names = children(existing) ?? new List<string>();
            }
            catch (Exception)
            {
                names = new List<string>();
            }
            if (names.Count > 0)
            {
                text.Append(" It holds: ").Append(string.Join(", ", names.Take(MaxListed).ToArray())).Append(names.Count > MaxListed ? ", ..." : "").Append('.');
            }
        }
        else if (IsWslPath(path))
        {
            text.Append("\n\nNothing of this WSL path can be reached. The distribution has to be running: start it (for example \"wsl -d <name>\" in a console, the name is in \"wsl -l -v\"), check that the name after \\\\wsl.localhost\\ is exactly that one, and add the folder again.");
        }
        else if (network)
        {
            text.Append("\n\nThe computer or the share \\\\").Append(string.Join("\\", parts.Take(2).ToArray())).Append(" cannot be reached.");
        }
        return text.ToString();
    }
}
