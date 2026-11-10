using System;
using System.Linq;
using System.Text.RegularExpressions;
using VisualTTY;

namespace SmarTTY;

/// <summary>
/// A package manager that can install a package file that was uploaded to the host (the "Install package file..." button of the
/// package manager window): which files it takes and the command that installs them. Not every package manager has it: xbps,
/// Portage, Homebrew and winget install from repositories only, so the button is not offered for them.
/// </summary>
public interface ILocalPackageFileInstaller
{
    /// <summary>The filter of the dialog that picks the files, in the form of a file dialog: "Debian packages (*.deb)|*.deb".</summary>
    string LocalPackageFileFilter { get; }

    /// <summary>The command of the package manager as the messages name it ("apt-get", "dnf", ...).</summary>
    string InstallToolName { get; }

    /// <summary>True for the name of a file that this package manager installs.</summary>
    bool IsPackageFile(string fileName);

    /// <summary>
    /// The command that installs the package files (paths on the host), in one run so that packages that depend on each other are
    /// resolved together; null when there is nothing to install. It is not wrapped in sudo: <see cref="LocalPackageFiles.BuildInstallLine"/> does that.
    /// </summary>
    string GetCommandLineForPackageFiles(string[] remotePaths, PackageCommitOptions options);
}

/// <summary>Pure logic around installing uploaded package files, tested by tests/ApkPackageManagerTests.</summary>
public static class LocalPackageFiles
{
    /// <summary>The directory of the host that the package files are uploaded to: it exists on every Unix host and everyone can read it.</summary>
    public const string RemoteDirectory = "/tmp";

    /// <summary>
    /// The start of the name of the directory made for one upload (<c>mktemp -d /tmp/smartty-pkg.XXXXXX</c>). The files go into a directory of
    /// their own instead of /tmp itself: a name that is predictable (/tmp/&lt;file name&gt;) lets another user on a shared machine put a file there
    /// first, which is then installed as root, and two files of the same name from different folders would overwrite each other.
    /// </summary>
    public const string UploadDirectoryPrefix = RemoteDirectory + "/smartty-pkg.";

    /// <summary>The command that makes the directory of an upload and prints its path (readable by all, writable by the user only).</summary>
    public const string MakeUploadDirectoryCommand = "d=$(mktemp -d " + UploadDirectoryPrefix + "XXXXXX) && chmod 755 \"$d\" && echo \"$d\"";

    /// <summary>The directory made by <see cref="MakeUploadDirectoryCommand"/> from its output, or null when the output is not such a path.</summary>
    public static string ParseUploadDirectory(System.Collections.Generic.IEnumerable<string> lines)
    {
        string path = lines?.Select(line => line?.Trim()).FirstOrDefault(line => !string.IsNullOrEmpty(line) && line.StartsWith(UploadDirectoryPrefix, StringComparison.Ordinal));
        return path != null && IsSafeFileName(path.Substring(RemoteDirectory.Length + 1)) ? path : null;
    }

    /// <summary>The upload directory that the files are in (to be removed after them), or null when they are not in one made for it.</summary>
    public static string UploadDirectoryOf(string[] remotePaths)
    {
        string first = remotePaths?.FirstOrDefault(path => !string.IsNullOrEmpty(path));
        if (first == null || !first.StartsWith(UploadDirectoryPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        int slash = first.LastIndexOf('/');
        string directory = first.Substring(0, slash);
        return slash > RemoteDirectory.Length && directory.IndexOf('/', RemoteDirectory.Length + 1) < 0
            && IsSafeFileName(directory.Substring(RemoteDirectory.Length + 1)) ? directory : null;
    }

    /// <summary>"rm" of the uploaded files and, when they were in a directory of their own, of the directory.</summary>
    public static string BuildRemoveCommand(string[] remotePaths)
    {
        string directory = UploadDirectoryOf(remotePaths);
        return "rm -f -- " + QuoteAll(remotePaths) + (directory == null ? "" : "; rmdir -- " + Quote(directory));
    }

    // A name that needs no care in a shell, also inside the double quotes of "sudo su -c": no space, quote, $ or backtick.
    private static readonly Regex SafeFileNameRegex = new Regex(@"^[A-Za-z0-9._+~:@%,=][A-Za-z0-9._+~:@%,=\-]*$", RegexOptions.Compiled);

    /// <summary>True if the file name ends with one of the extensions (case-insensitive).</summary>
    public static bool HasExtension(string fileName, params string[] extensions)
    {
        return fileName != null && extensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True if the name of a file can be used as it is in the commands that install it. A package file name is made of letters,
    /// digits and "._+-~:@%,=" (that is what the names of Debian, RPM, Arch, opkg, apk and FreeBSD packages are made of);
    /// the file is uploaded under its own name, so one with a space or a shell character must be renamed first.
    /// </summary>
    public static bool IsSafeFileName(string fileName)
    {
        return fileName != null && SafeFileNameRegex.IsMatch(fileName);
    }

    public static bool HasFiles(string[] remotePaths)
    {
        return remotePaths != null && remotePaths.Any(path => !string.IsNullOrEmpty(path));
    }

    /// <summary>
    /// The paths in single quotes, separated by blanks. A path that is not absolute gets "./" in front: apt, dnf, zypper and apk
    /// read a name without a slash as a package name of a repository, not as a file.
    /// </summary>
    public static string QuoteAll(string[] remotePaths)
    {
        return string.Join(" ", (remotePaths ?? new string[0]).Where(path => !string.IsNullOrEmpty(path)).Select(path => Quote(path.StartsWith("/") || path.StartsWith("./") ? path : "./" + path)));
    }

    private static string Quote(string value)
    {
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// The line that is typed in the terminal: the install command with the rights it needs (<see cref="PackageIndexUpdater.WrapForPrivileges"/>),
    /// then the removal of the uploaded files. They are removed after a failed installation too, because the user
    /// picks them again; "rm" is not part of the sudo command, as the user owns the files in /tmp. Null when there is nothing to install.
    /// </summary>
    public static string BuildInstallLine(ILocalPackageFileInstaller installer, string[] remotePaths, PackageCommitOptions options, bool isRoot)
    {
        string command = installer?.GetCommandLineForPackageFiles(remotePaths, options);
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }
        return PackageIndexUpdater.WrapForPrivileges(command, isRoot) + "; " + BuildRemoveCommand(remotePaths);
    }
}

/// <summary>
/// What the package manager window asks for when the user picks package files from this computer, or downloads them from links
/// ("Install from URL..."): they are uploaded and installed.
/// </summary>
public sealed class LocalPackageInstallRequest
{
    public LocalPackageInstallRequest(string[] localFiles, ILocalPackageFileInstaller installer, PackageCommitOptions options, bool isRoot, string temporaryDirectory = null)
    {
        LocalFiles = localFiles;
        Installer = installer;
        Options = options;
        IsRoot = isRoot;
        TemporaryDirectory = temporaryDirectory;
    }

    /// <summary>
    /// The folder of this computer that holds the files when they were downloaded from links (<see cref="PackageUrlDownloader"/>), or null
    /// for files that the user picked on the disk: it is removed with the files when their upload has ended.
    /// </summary>
    public string TemporaryDirectory { get; }

    /// <summary>Full paths of the files on this computer.</summary>
    public string[] LocalFiles { get; }

    public ILocalPackageFileInstaller Installer { get; }

    /// <summary>A copy of the options of the window at the time of the choice (the check box of the confirmations).</summary>
    public PackageCommitOptions Options { get; }

    /// <summary>True when the commands run as root, so that they need no sudo.</summary>
    public bool IsRoot { get; }
}
