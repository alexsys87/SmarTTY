using System;
using System.Linq;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// The parts of Windows host support that need no server: the SFTP form of Windows paths, which key file sshd of Windows uses
/// (read from whoami and sshd_config) and the icacls command that closes it. Always run, like the SSH.NET contract checks.
/// </summary>
internal static class WindowsHostTests
{
    public static void Run()
    {
        Test.Section("Windows host: paths, key file, icacls (no server needed)");
        Paths();
        Administrator();
        SshdConfig();
        Icacls();
        Lines();
    }

    private static void Paths()
    {
        foreach ((string input, string expected) in new[]
        {
            (@"C:\Users\me", "/C:/Users/me"),
            ("c:/Users//me/", "/C:/Users/me"),
            ("/c:/Users/me", "/C:/Users/me"),
            ("C:", "/C:/"),
            ("/C:", "/C:/"),
            ("/C:/", "/C:/"),
            ("/", "/"),
            ("", "/"),
            ("/C:/Users/./me/../you", "/C:/Users/you"),
            ("/C:/..", "/"),
            ("/../..", "/"),
            ("/home/user//x/", "/home/user/x"),
        })
        {
            Test.Equal(expected, WindowsSftpPath.Normalize(input), "Normalize '" + input + "'");
        }
        Test.Equal("/C:/Users/me/Documents", WindowsSftpPath.Combine("/C:/Users/me", "Documents"), "Combine: a name goes into the directory");
        Test.Equal("/C:/Users", WindowsSftpPath.Combine("/C:/Users/me", ".."), "Combine: \"..\" goes up");
        Test.Equal("/C:/", WindowsSftpPath.Combine("/C:/Users", ".."), "Combine: up from a folder of the drive is the root of the drive");
        Test.Equal("/", WindowsSftpPath.Combine("/C:/", ".."), "Combine: up from the root of a drive is the place above the drives");
        Test.Equal("/D:/data", WindowsSftpPath.Combine("/C:/Users/me", @"D:\data"), "Combine: an absolute Windows path stands for itself");
        Test.Equal("/C:/Windows", WindowsSftpPath.Combine("/D:/x", "/c:/Windows"), "Combine: an absolute SFTP path stands for itself");
        Test.Equal("/C:/Users", WindowsSftpPath.Combine("/C:/Users", ""), "Combine: nothing to add gives the directory itself");
        Test.Equal("/C:/", WindowsSftpPath.Parent("/C:/Users"), "Parent of a folder of the drive");
        Test.Equal("/", WindowsSftpPath.Parent("/"), "Parent of the root is the root");
        Test.Check(WindowsSftpPath.IsDriveRoot("/C:") && WindowsSftpPath.IsDriveRoot("/c:/") && !WindowsSftpPath.IsDriveRoot("/C:/Users") && !WindowsSftpPath.IsDriveRoot("/"), "IsDriveRoot");
        Test.Check(WindowsSftpPath.IsRoot("/") && !WindowsSftpPath.IsRoot("/C:/"), "IsRoot");
        Test.Equal(@"C:\Users\me", WindowsSftpPath.ToWindows("/C:/Users/me"), "ToWindows of a profile");
        Test.Equal(@"C:\", WindowsSftpPath.ToWindows("/C:/"), "ToWindows of a drive root");
        Test.Check(WindowsSftpPath.ToWindows("/") == null && WindowsSftpPath.ToWindows("/home/user") == null, "ToWindows is null without a drive");
        Test.Equal("/C:/", WindowsSftpPath.ForServer("/C:"), "a bare drive is sent with its slash");
        Test.Equal("/C:/Users", WindowsSftpPath.ForServer("/C:/Users"), "any other path is sent as it is");
        Test.Equal("/home/x", WindowsSftpPath.ForServer("/home/x"), "also a Unix path");
        Test.Equal("/C:/x", SftpFileSystem.Child("/C:/", "x"), "a child of a drive root");
        Test.Equal("/x", SftpFileSystem.Child("/", "x"), "a child of the root");
        Test.Equal("/C:/Users/x", SftpFileSystem.Child("/C:/Users", "x"), "a child of a folder");
    }

    private static void Administrator()
    {
        string[] english =
        {
            "GROUP INFORMATION",
            "-----------------",
            "",
            "Group Name                             Type             SID          Attributes",
            "====================================== ================ ============ ==================================================",
            "Everyone                               Well-known group S-1-1-0      Mandatory group, Enabled by default, Enabled group",
            "BUILTIN\\Administrators                 Alias            S-1-5-32-544 Mandatory group, Enabled by default, Enabled group, Group owner",
        };
        string[] german =
        {
            "VORDEFINIERT\\Administratoren           Alias            S-1-5-32-544 Gruppe wird nur f\u00fcr Deny verwendet",
        };
        string[] user =
        {
            "BUILTIN\\Users                         Alias            S-1-5-32-545 Mandatory group, Enabled by default, Enabled group",
            "NT AUTHORITY\\Authenticated Users       Well-known group S-1-5-11     Mandatory group, Enabled by default, Enabled group",
        };
        Test.Check(WindowsAuthorizedKeys.IsAdministratorListed(english), "whoami /groups of an administrator (English) lists the Administrators SID");
        Test.Check(WindowsAuthorizedKeys.IsAdministratorListed(german), "a translated group name and a group \"for deny only\" are found by the SID alone");
        Test.Check(WindowsAuthorizedKeys.IsAdministratorListed(new[] { "\"BUILTIN\\Administrators\",\"Alias\",\"S-1-5-32-544\",\"Mandatory group\"" }), "the CSV form is found too");
        Test.Check(!WindowsAuthorizedKeys.IsAdministratorListed(user), "an ordinary user is not an administrator");
        Test.Check(!WindowsAuthorizedKeys.IsAdministratorListed(new[] { "Group S-1-5-32-5440 x", "S-1-5-32-5445" }) && !WindowsAuthorizedKeys.IsAdministratorListed(null) && !WindowsAuthorizedKeys.IsAdministratorListed(new string[0]), "a longer SID that starts with it, no lines and no output are not administrators");
    }

    private static void SshdConfig()
    {
        Test.Equal("/C:/ProgramData/ssh/administrators_authorized_keys", WindowsAuthorizedKeys.ParseAdminKeysFile(new[]
        {
            "# Authentication:", "#PermitRootLogin prohibit-password", "AuthorizedKeysFile .ssh/authorized_keys", "",
            "Match Group administrators", "       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys"
        }), "the block of the default sshd_config of Windows");
        Test.Equal("/C:/ProgramData/ssh/administrators_authorized_keys", WindowsAuthorizedKeys.ParseAdminKeysFile(new[]
        {
            "match group Administrators", "\tauthorizedkeysfile  __PROGRAMDATA__/ssh/administrators_authorized_keys  # admins"
        }), "keywords in any case, a tab, a trailing comment");
        Test.Check(WindowsAuthorizedKeys.ParseAdminKeysFile(new[] { "AuthorizedKeysFile .ssh/authorized_keys" }) == null, "no Match block: the administrators use their own file");
        Test.Check(WindowsAuthorizedKeys.ParseAdminKeysFile(new[] { "#Match Group administrators", "#       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys" }) == null, "a commented-out block does not count");
        Test.Check(WindowsAuthorizedKeys.ParseAdminKeysFile(new[] { "Match Group administrators", "    PasswordAuthentication no" }) == null, "a block for administrators without AuthorizedKeysFile: their own file");
        Test.Check(WindowsAuthorizedKeys.ParseAdminKeysFile(new[] { "Match User bob", "    AuthorizedKeysFile bob_keys", "Match Group staff", "    AuthorizedKeysFile staff_keys" }) == null, "AuthorizedKeysFile in the block of another user or group is not the administrators'");
        Test.Check(WindowsAuthorizedKeys.ParseAdminKeysFile(new[] { "Match Group administrators", "    X11Forwarding no", "Match all", "    AuthorizedKeysFile everyone_keys" }) == null, "a line after the end of the block does not count");
        Test.Equal("/D:/keys/admins", WindowsAuthorizedKeys.ParseAdminKeysFile(new[] { "Match Group users,administrators", "    AuthorizedKeysFile \"D:/keys/admins\"" }), "a group list with administrators in it, a quoted drive path");
        Test.Equal("/E:/PD/ssh/admin_keys", WindowsAuthorizedKeys.ParseAdminKeysFile(new[] { "Match Group administrators", "AuthorizedKeysFile __PROGRAMDATA__/ssh/admin_keys" }, "/E:/PD"), "__PROGRAMDATA__ is the ProgramData that was given");
        Test.Check(WindowsAuthorizedKeys.ParseAdminKeysFile(null) == null && WindowsAuthorizedKeys.ParseAdminKeysFile(new string[0]) == null, "no lines");
        Test.Equal("/C:/ProgramData/ssh/administrators_authorized_keys", WindowsAuthorizedKeys.DefaultAdminKeysFile(), "the default file of the administrators");
        Test.Equal("/C:/ProgramData/ssh/sshd_config", WindowsAuthorizedKeys.SshdConfigFile(), "the default sshd_config");
    }

    private static void Icacls()
    {
        Test.Equal("icacls \"C:\\ProgramData\\ssh\\administrators_authorized_keys\" /inheritance:r /grant *S-1-5-32-544:F /grant *S-1-5-18:F", WindowsAuthorizedKeys.RestrictAccessCommand("/C:/ProgramData/ssh/administrators_authorized_keys"), "icacls removes the inherited rights and leaves Administrators and SYSTEM, by SID");
        Test.Equal("icacls \"D:\\My Keys\\admins\" /inheritance:r /grant *S-1-5-32-544:F /grant *S-1-5-18:F", WindowsAuthorizedKeys.RestrictAccessCommand("/D:/My Keys/admins"), "a path with a space is in double quotes");
        Test.Throws<ArgumentException>(() => WindowsAuthorizedKeys.RestrictAccessCommand("/home/user/keys"), "a path without a drive is refused");
    }

    private static void Lines()
    {
        Test.Equal("a|b|", string.Join("|", WindowsAuthorizedKeys.SplitLines(System.Text.Encoding.UTF8.GetBytes("a\r\nb\r\n\r\n"))), "CRLF lines, the empty line before the end is kept and the end is not a line");
        Test.Equal("a|b", string.Join("|", WindowsAuthorizedKeys.SplitLines(System.Text.Encoding.UTF8.GetBytes("a\nb"))), "LF lines without a newline at the end");
        Test.Check(WindowsAuthorizedKeys.SplitLines(new byte[0]).Count == 0 && WindowsAuthorizedKeys.SplitLines(null).Count == 0, "an empty file has no lines");
    }
}
