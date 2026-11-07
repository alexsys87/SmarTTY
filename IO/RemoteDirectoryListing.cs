using SmarTTY.Utilities;
using System.Collections.Generic;

namespace SmarTTY.IO;

/// <summary>
/// The names of the files and of the folders directly inside a remote folder, for the download that goes file by file.
/// "ls -F" cannot be used for this: its marks (/ * @ = | &gt;) are appended to the name, so a plain file called "a=" or
/// "a|" looks like a socket or a pipe and was skipped, and a file "a*" that is not executable looked like an executable "a"
/// and was fetched under the wrong name. The shell tells what an entry is with "test -d" and "test -f" (both follow
/// symbolic links, as the download does), so every name is printed after its own letter and nothing is guessed from its end.
/// </summary>
public sealed class RemoteDirectoryListing
{
    /// <summary>The line of a folder: the letter, a blank, the name.</summary>
    public const string DirectoryPrefix = "d ";

    /// <summary>The line of a regular file (or a link to one).</summary>
    public const string FilePrefix = "f ";

    /// <summary>Printed (without the name) for an entry whose name holds a line break: it would be several lines.</summary>
    public const string LineBreakMark = "!";

    public readonly List<string> Files = new List<string>();

    public readonly List<string> Directories = new List<string>();

    /// <summary>How many entries were left out because their name holds a line break (Windows cannot hold such a name either).</summary>
    public int NamesWithLineBreak;

    /// <summary>
    /// The command line that prints the entries of <paramref name="remoteDir"/>, hidden ones included. Pipes, sockets, devices
    /// and broken links are neither a file nor a folder and are not printed: reading a pipe would never end.
    /// </summary>
    public static string BuildCommand(string remoteDir)
    {
        // $nl is a line feed made without writing one into the script: the command has to be one line, because the login
        // shell of the user (tcsh, for one) reads a line feed inside quotes as the end of the command.
        string script = "nl=$(printf '\\nx'); nl=${nl%x}; cd " + RemotePathUtils.ShellQuote(remoteDir)
            + " || exit 1; for f in .[!.]* ..?* *; do case $f in *\"$nl\"*) printf '" + LineBreakMark + "\\n';;"
            + " *) if [ -d \"$f\" ]; then printf '" + DirectoryPrefix + "%s\\n' \"$f\";"
            + " elif [ -f \"$f\" ]; then printf '" + FilePrefix + "%s\\n' \"$f\"; fi;; esac; done";
        return PosixShell.Wrap(script);
    }

    /// <summary>Sorts the output of <see cref="BuildCommand"/>; a line it does not know is ignored.</summary>
    public static RemoteDirectoryListing Parse(IEnumerable<string> lines)
    {
        RemoteDirectoryListing listing = new RemoteDirectoryListing();
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            if (line == LineBreakMark)
            {
                listing.NamesWithLineBreak++;
            }
            else if (line.Length > DirectoryPrefix.Length && line.StartsWith(DirectoryPrefix, System.StringComparison.Ordinal))
            {
                listing.Directories.Add(line.Substring(DirectoryPrefix.Length));
            }
            else if (line.Length > FilePrefix.Length && line.StartsWith(FilePrefix, System.StringComparison.Ordinal))
            {
                listing.Files.Add(line.Substring(FilePrefix.Length));
            }
        }
        return listing;
    }
}
