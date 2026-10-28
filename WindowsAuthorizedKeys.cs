using SmarTTY.Ssh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY;

/// <summary>
/// Installing, checking and removing a public key on a Windows host (Win32-OpenSSH). <see cref="AuthorizedKeysManager"/> does
/// it with sh, test, mkdir, chmod and mv, none of which a Windows host has, so here the key files are read and written over
/// SFTP, and the one command that is needed (icacls) is sent as it is to the cmd.exe or PowerShell the server starts.
/// Where the keys go differs from Unix: sshd of Windows reads the keys of the members of the Administrators group from
/// one file for all of them, %ProgramData%\ssh\administrators_authorized_keys, and refuses it unless only Administrators
/// and SYSTEM may touch it. The keys of the other users are in the usual .ssh\authorized_keys of the profile.
/// </summary>
internal static class WindowsAuthorizedKeys
{
    public const string AdministratorsSid = "S-1-5-32-544";

    public const string SystemSid = "S-1-5-18";

    /// <summary>
    /// %ProgramData% as SFTP shows it. The system drive is taken as C: (SFTP has no variables to ask for it); the ssh directory
    /// in it is checked before it is used.
    /// </summary>
    public const string DefaultProgramData = "/C:/ProgramData";

    /// <summary>Where sshd of Windows keeps the keys of the administrators with its default configuration.</summary>
    public static string DefaultAdminKeysFile(string programData = DefaultProgramData)
    {
        return SftpFileSystem.Child(programData, "ssh/administrators_authorized_keys");
    }

    public static string SshdConfigFile(string programData = DefaultProgramData)
    {
        return SftpFileSystem.Child(programData, "ssh/sshd_config");
    }

    /// <summary>A key file and whether the server wants it closed to everybody but Administrators and SYSTEM.</summary>
    internal sealed class KeyFile
    {
        public string Path;

        public bool RestrictAccess;
    }

    #region Pure parts (tests/WindowsHostTests)

    /// <summary>
    /// True when the output of "whoami /groups" lists the Administrators group. Only the SID is looked for: the name of the
    /// group and the words of the attribute column are translated, the SID is not. Membership (not elevation) is what sshd
    /// goes by when it picks the key file, so a group that is listed as "deny only" counts too.
    /// </summary>
    public static bool IsAdministratorListed(IEnumerable<string> whoamiGroupsOutput)
    {
        // The SID as a whole: a longer one that merely starts with it (S-1-5-32-5440) is another group.
        return whoamiGroupsOutput != null && whoamiGroupsOutput.Any(line => line != null && Regex.IsMatch(line, "(?<![\\w-])" + AdministratorsSid + "(?![\\w-])", RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// The key file sshd reads for the members of the Administrators group, from the lines of sshd_config: the path in the
    /// AuthorizedKeysFile line of the "Match Group administrators" block, or null when there is no such block (the
    /// administrators then use their own .ssh\authorized_keys like everybody else).
    /// </summary>
    public static string ParseAdminKeysFile(IEnumerable<string> sshdConfigLines, string programData = DefaultProgramData)
    {
        bool inAdministratorsBlock = false;
        foreach (string rawLine in sshdConfigLines ?? new string[0])
        {
            string line = rawLine ?? "";
            int comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line.Substring(0, comment);
            }
            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            Match match = Regex.Match(line, @"^Match\s+(?<criteria>.*)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                inAdministratorsBlock = Regex.IsMatch(match.Groups["criteria"].Value, @"^Group\s+(?:\S*,)?administrators(?:,\S*)?(\s|$)", RegexOptions.IgnoreCase);
                continue;
            }
            if (!inAdministratorsBlock)
            {
                continue;
            }
            match = Regex.Match(line, @"^AuthorizedKeysFile\s+(?<files>.+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }
            string file = match.Groups["files"].Value.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim('"');
            file = file.Replace("__PROGRAMDATA__", programData.TrimStart('/'));
            return WindowsSftpPath.Normalize(file);
        }
        return null;
    }

    /// <summary>
    /// The icacls command that leaves a key file to Administrators and SYSTEM. The groups are given as SIDs, because
    /// "Administrators" is called otherwise on a localised Windows; the path is in double quotes, which cmd.exe and
    /// PowerShell read alike.
    /// </summary>
    public static string RestrictAccessCommand(string sftpPath)
    {
        string windowsPath = WindowsSftpPath.ToWindows(sftpPath) ?? throw new ArgumentException("Not a path with a drive: " + sftpPath, nameof(sftpPath));
        return "icacls \"" + windowsPath + "\" /inheritance:r /grant *" + AdministratorsSid + ":F /grant *" + SystemSid + ":F";
    }

    /// <summary>The lines of a key file (CRLF or LF).</summary>
    public static List<string> SplitLines(byte[] content)
    {
        string text = new UTF8Encoding(false).GetString(content ?? new byte[0]);
        List<string> lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    #endregion

    #region Key files over SFTP (tests/SshNetIntegrationTests)

    /// <summary>The content of a file, or null when it does not exist.</summary>
    internal static byte[] ReadFile(SftpChannel sftp, string path)
    {
        try
        {
            using Stream stream = sftp.OpenRead(path, 0);
            using MemoryStream buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (SftpStatusException exception) when (exception.Status == SftpChannel.StatusNoSuchFile)
        {
            return null;
        }
    }

    internal static bool FileHoldsKey(SftpChannel sftp, string path, byte[] keyBlob)
    {
        byte[] content = ReadFile(sftp, path);
        return content != null && SplitLines(content).Any(line => AuthorizedKeysFile.LineHoldsKey(line, keyBlob));
    }

    /// <summary>
    /// Adds the key line to the file (creating it) unless the key is already in it. Returns whether the file was written.
    /// The other lines and their line ending stay as they are.
    /// </summary>
    internal static bool AddKeyToFile(SftpChannel sftp, string path, string keyLine, byte[] keyBlob)
    {
        byte[] content = ReadFile(sftp, path) ?? new byte[0];
        if (SplitLines(content).Any(line => AuthorizedKeysFile.LineHoldsKey(line, keyBlob)))
        {
            return false;
        }
        string eol = ContainsCrLf(content) ? "\r\n" : "\n";
        List<byte> data = new List<byte>(content);
        if (data.Count > 0 && data[data.Count - 1] != (byte)'\n')
        {
            data.AddRange(Encoding.ASCII.GetBytes(eol));
        }
        data.AddRange(new UTF8Encoding(false).GetBytes(keyLine + eol));
        ReplaceFile(sftp, path, data.ToArray());
        return true;
    }

    /// <summary>Removes the lines of this key (and nothing else) from the file. Returns whether the file was written.</summary>
    internal static bool RemoveKeyFromFile(SftpChannel sftp, string path, byte[] keyBlob)
    {
        byte[] content = ReadFile(sftp, path);
        if (content == null)
        {
            return false;
        }
        List<string> remaining = AuthorizedKeysFile.RemoveKey(SplitLines(content), keyBlob, out bool removed);
        if (!removed)
        {
            return false;
        }
        string eol = ContainsCrLf(content) ? "\r\n" : "\n";
        StringBuilder text = new StringBuilder();
        foreach (string line in remaining)
        {
            text.Append(line).Append(eol);
        }
        ReplaceFile(sftp, path, new UTF8Encoding(false).GetBytes(text.ToString()));
        return true;
    }

    private static bool ContainsCrLf(byte[] content)
    {
        for (int index = 0; index + 1 < content.Length; index++)
        {
            if (content[index] == '\r' && content[index + 1] == '\n')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Replaces the content of a file without a moment in which it is half written: the new content goes to a file beside
    /// it, and only when that is complete the old one is removed (SFTP version 3 does not rename over an existing file) and
    /// the new one takes its name. A write that fails (no space on the disk) leaves the old file alone.
    /// </summary>
    internal static void ReplaceFile(SftpChannel sftp, string path, byte[] content)
    {
        string temporary = path + ".smartty";
        using (Stream stream = sftp.OpenWrite(temporary, 0, truncate: true, mode: Convert.ToInt32("600", 8)))
        {
            stream.Write(content, 0, content.Length);
        }
        long? size = sftp.Stat(temporary)?.Size;
        if (size != content.Length)
        {
            throw new RemoteOperationException("Only " + (size ?? 0) + " of " + content.Length + " bytes of " + path + " were written; the file was not changed.");
        }
        if (sftp.Stat(path) != null)
        {
            sftp.Remove(path);
        }
        try
        {
            sftp.Rename(temporary, path);
        }
        catch (Exception exception) when (!(exception is IOException))
        {
            throw new RemoteOperationException("Cannot put the new " + path + " in place: " + exception.Message + " The new content is in " + temporary + ".");
        }
    }

    /// <summary>The directory exists afterwards; created when it was missing.</summary>
    internal static void EnsureDirectory(SftpChannel sftp, string path)
    {
        SftpFileAttributes attributes = sftp.Stat(path);
        if (attributes == null)
        {
            sftp.MakeDirectory(path);
        }
        else if (!attributes.IsDirectory)
        {
            throw new RemoteOperationException(path + " is not a directory.");
        }
    }

    #endregion

    #region Public entry points (the same three as in AuthorizedKeysManager)

    private static bool IsAdministrator(SshSession session)
    {
        List<string> output = session.RunCommandChecked("whoami /groups", requireSuccess: true);
        if (output == null)
        {
            // A lost connection is not "the user is no administrator".
            session.ThrowIfConnectionLost();
            throw new RemoteOperationException("Cannot tell whether the user is an administrator: \"whoami /groups\" failed. The key file depends on it.");
        }
        return IsAdministratorListed(output);
    }

    /// <summary>
    /// The file sshd reads the keys of the logged in user from: .ssh\authorized_keys of the profile (the directory the SFTP
    /// server starts in), and for a member of the Administrators group the common file of the administrators, which is
    /// found in sshd_config, or assumed at its default place when sshd_config cannot be read and the ssh directory is there.
    /// </summary>
    internal static List<KeyFile> FindKeyFiles(SftpChannel sftp, bool isAdministrator, string programData = DefaultProgramData)
    {
        KeyFile userFile = new KeyFile { Path = SftpFileSystem.Child(sftp.RealPath("."), ".ssh/authorized_keys") };
        if (!isAdministrator)
        {
            return new List<KeyFile> { userFile };
        }
        string adminFile = DefaultAdminKeysFile(programData);
        byte[] config = null;
        try
        {
            config = ReadFile(sftp, SshdConfigFile(programData));
        }
        catch (SftpStatusException)
        {
            // Not readable for this user: the default installation is assumed.
        }
        if (config != null)
        {
            adminFile = ParseAdminKeysFile(SplitLines(config), programData);
        }
        else if (sftp.Stat(WindowsSftpPath.Parent(adminFile)) == null)
        {
            adminFile = null;
        }
        return new List<KeyFile> { adminFile == null ? userFile : new KeyFile { Path = adminFile, RestrictAccess = true } };
    }

    /// <summary>
    /// Puts the key into each of the files (creating the directory of a file when needed), calls <paramref name="restrictAccess"/>
    /// for a file that was written and has to be closed to everybody but the administrators, and reads every file back.
    /// </summary>
    internal static void AppendToFiles(SftpChannel sftp, List<KeyFile> files, string keyLine, byte[] keyBlob, Action<string> restrictAccess)
    {
        foreach (KeyFile file in files)
        {
            EnsureDirectory(sftp, WindowsSftpPath.Parent(file.Path));
            bool written = AddKeyToFile(sftp, file.Path, keyLine, keyBlob);
            if (written && file.RestrictAccess)
            {
                restrictAccess(file.Path);
            }
        }
        // Only reading it back tells whether the server will find the key.
        foreach (KeyFile file in files)
        {
            if (!FileHoldsKey(sftp, file.Path, keyBlob))
            {
                throw new RemoteOperationException("The key was not found in " + file.Path + " after adding it");
            }
        }
    }

    internal static void RemoveFromFiles(SftpChannel sftp, List<KeyFile> files, byte[] keyBlob, Action<string> restrictAccess)
    {
        foreach (KeyFile file in files)
        {
            if (RemoveKeyFromFile(sftp, file.Path, keyBlob) && file.RestrictAccess)
            {
                restrictAccess(file.Path);
            }
        }
    }

    private static void RestrictAccess(SshSession session, string path)
    {
        List<string> output = session.RunCommandChecked(RestrictAccessCommand(path), requireSuccess: true);
        if (output == null)
        {
            session.ThrowIfConnectionLost();
            throw new RemoteOperationException("Cannot restrict the access to " + path + " (icacls failed); sshd would ignore the keys in it.");
        }
    }

    public static bool IsKeyAuthorized(SshSession session, byte[] keyBlob)
    {
        try
        {
            using SftpChannel sftp = SftpChannel.Open(session);
            return FindKeyFiles(sftp, IsAdministrator(session)).All(file => FileHoldsKey(sftp, file.Path, keyBlob));
        }
        catch (IOException) when (session.IsConnectionClosed)
        {
            throw;
        }
        catch
        {
            session.ThrowIfConnectionLost();
            return false;
        }
    }

    public static void Append(SshSession session, string keyLine, byte[] keyBlob)
    {
        using SftpChannel sftp = SftpChannel.Open(session);
        AppendToFiles(sftp, FindKeyFiles(sftp, IsAdministrator(session)), keyLine, keyBlob, path => RestrictAccess(session, path));
    }

    public static void Remove(SshSession session, byte[] keyBlob)
    {
        using SftpChannel sftp = SftpChannel.Open(session);
        RemoveFromFiles(sftp, FindKeyFiles(sftp, IsAdministrator(session)), keyBlob, path => RestrictAccess(session, path));
    }

    #endregion
}
