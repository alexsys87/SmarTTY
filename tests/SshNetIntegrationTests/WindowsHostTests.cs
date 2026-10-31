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
        ProgramData();
        Prompts();
        Certutil();
        IdlePrompt();
        Sync();
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

    private static void ProgramData()
    {
        Test.Equal("/C:/ProgramData", WindowsAuthorizedKeys.ParseProgramData(new[] { "C:\\ProgramData $env:ProgramData" }), "ProgramData from cmd.exe (it prints the path, and the PowerShell word as it was typed)");
        Test.Equal("/C:/ProgramData", WindowsAuthorizedKeys.ParseProgramData(new[] { "%ProgramData% C:\\ProgramData" }), "ProgramData from PowerShell");
        Test.Equal("/D:/Program Data/x", WindowsAuthorizedKeys.ParseProgramData(new[] { "", "D:\\Program Data\\x $env:ProgramData" }), "a ProgramData on another drive, with a space in it");
        Test.Equal("/E:/PD", WindowsAuthorizedKeys.ParseProgramData(new[] { "%ProgramData% E:\\PD\\" }), "a trailing backslash goes");
        Test.Check(WindowsAuthorizedKeys.ParseProgramData(new[] { "%ProgramData% $env:ProgramData" }) == null, "neither shell expanded it: no path");
        Test.Check(WindowsAuthorizedKeys.ParseProgramData(new[] { "'echo' is not recognized as an internal or external command," }) == null && WindowsAuthorizedKeys.ParseProgramData(null) == null, "an error message and no output: no path");
        Test.Check(WindowsAuthorizedKeys.ParseProgramData(new[] { "/usr/share $env:ProgramData" }) == null, "a path without a drive is not taken");
    }

    private static string[] Reports(params string[] chunks)
    {
        System.Collections.Generic.List<string> reported = new System.Collections.Generic.List<string>();
        SmarTTY.Terminal.TerminalDirectoryWatcher watcher = new SmarTTY.Terminal.TerminalDirectoryWatcher();
        watcher.DirectoryReported += reported.Add;
        foreach (string chunk in chunks)
        {
            watcher.Observe(chunk);
        }
        return reported.ToArray();
    }

    private static void Prompts()
    {
        string esc = "\u001b";
        Test.Equal(@"C:\Users\me", string.Join("|", Reports("Microsoft Windows [Version 10.0.19045.3693]\r\n(c) Microsoft Corporation. All rights reserved.\r\n\r\nC:\\Users\\me>")), "the prompt of cmd.exe");
        Test.Equal(@"C:\Windows\System32", string.Join("|", Reports("PS C:\\Windows\\System32> ")), "the prompt of PowerShell");
        Test.Equal(@"C:\Program Files (x86)\App", string.Join("|", Reports(esc + "[32mPS " + esc + "[0mC:\\Program Files (x86)\\App" + esc + "[0m> " + esc + "[?25h")), "colours and cursor sequences in the prompt, a path with spaces and brackets");
        Test.Equal(@"C:\", string.Join("|", Reports("C:\\>")), "the root of a drive");
        Test.Equal(@"C:\Users\me", string.Join("|", Reports("C:\\Us", "ers\\me>")), "a prompt split between two chunks");
        Test.Equal(@"C:\x", string.Join("|", Reports("hello\r\n" + esc + "[3", "2mC:\\x>")), "an escape sequence split between two chunks");
        Test.Equal(@"D:\two", string.Join("|", Reports("C:\\one>dir\r\n volume...\r\n\r\nC:\\one>cd /d D:\\two\r\n\r\nD:\\two>")), "of several prompts in one chunk the last one counts");
        Test.Equal(@"C:\one|D:\two", string.Join("|", Reports("C:\\one>dir\r\n volume...\r\n\r\nC:\\one>", "cd /d D:\\two\r\n\r\nD:\\two>")), "the prompt that is shown again is reported once, a new directory is reported");
        Test.Equal(@"C:\one", string.Join("|", Reports("C:\\one>", "dir", "\r\nDirectory of stuff\r\n")), "typing after a prompt, and output after it, report nothing more");
        Test.Equal(@"C:\a|C:\b|C:\a", string.Join("|", Reports("C:\\a>", "\r\nC:\\b>", "\r\nC:\\a>")), "going back to a directory is reported again");
        Test.Equal(@"C:\a|C:\b", string.Join("|", Reports("C:\\a>cls", esc + "[2J" + esc + "[HC:\\b>")), "after cls the prompt is at the top of the screen, with no newline before it");
        Test.Equal(@"C:\a|C:\b", string.Join("|", Reports("C:\\a>", esc + "[1;1HC:\\b>")), "a cursor move to another line starts a new line");
        Test.Equal("", string.Join("|", Reports("PS HKLM:\\SOFTWARE> ", "PS Microsoft.PowerShell.Core\\FileSystem::\\\\server\\share> ", "echo hi > C:\\x\r\n", "see C:\\x> not at the start\r\n")), "providers, UNC paths and text that is not at the start of a line are not prompts");
        Test.Equal(@"D:\Projects", string.Join("|", Reports(esc + "]9;9;\"D:\\Projects\"" + esc + "\\")), "OSC 9;9 of Windows Terminal, in quotes, ended with ESC \\");
        Test.Equal(@"D:\Projects", string.Join("|", Reports(esc + "]9;9;D:\\Projects\u0007")), "OSC 9;9 without quotes, ended with BEL");
        Test.Equal(@"D:\Projects", string.Join("|", Reports(esc + "]9;9;\"D:\\Proj", "ects\"" + esc, "\\")), "OSC 9;9 cut in the middle, also between ESC and the backslash");
        Test.Equal("/C:/Users/me x", string.Join("|", Reports(esc + "]7;file://PC/C:/Users/me%20x\u0007")), "OSC 7 with a file URL");
        Test.Equal("", string.Join("|", Reports(esc + "]7;file://host/home/user\u0007", esc + "]9;9;/not/windows\u0007", esc + "]0;C:\\Windows\\system32\\cmd.exe\u0007")), "a Unix OSC 7, a path without a drive and a window title are not directories");
        Test.Equal(@"C:\", string.Join("|", Reports(esc + "]0;C:\\Windows\\system32\\cmd.exe\u0007C:\\>")), "a title with a path in it does not hide the prompt after it");
        Test.Equal("", string.Join("|", Reports("", null, "plain output\r\n")), "empty chunks and plain output report nothing");
    }

    private static void IdlePrompt()
    {
        string esc = "\u001b";
        SmarTTY.Terminal.TerminalDirectoryWatcher watcher = new SmarTTY.Terminal.TerminalDirectoryWatcher();
        int ready = 0;
        watcher.PromptReady += () => ready++;
        Test.Check(!watcher.AtPrompt, "before any output the shell is not known to wait");
        watcher.Observe("Microsoft Windows [Version 10.0.19045.3693]\r\n\r\nC:\\Users\\me>");
        Test.Check(watcher.AtPrompt && watcher.PromptDirectory == @"C:\Users\me" && watcher.Shell == SmarTTY.Terminal.PromptShell.Cmd && ready == 1, "a cmd.exe prompt with nothing after it: waiting, with its directory and its shell, announced once");
        watcher.Observe(esc + "[?25h");
        Test.Check(watcher.AtPrompt && ready == 1, "a chunk with only escape sequences changes nothing");
        watcher.Observe("d");
        Test.Check(!watcher.AtPrompt, "a typed character after the prompt: no longer waiting");
        watcher.Observe("ir\r\n volume\r\n\r\nC:\\Users\\me>");
        Test.Check(watcher.AtPrompt && ready == 2, "the next prompt is waiting again, announced again");
        watcher.Observe("\r\nPS D:\\Work> ");
        Test.Check(watcher.AtPrompt && watcher.PromptDirectory == @"D:\Work" && watcher.Shell == SmarTTY.Terminal.PromptShell.PowerShell && ready == 3, "a PowerShell prompt, with the space after it");
        watcher.Observe("\r\nPS HKLM:\\SOFTWARE> ");
        Test.Check(!watcher.AtPrompt, "the prompt of the registry provider is not a place to type cd into");
        watcher.Observe("\r\nPS C:\\a> ");
        watcher.Observe("\r\n");
        Test.Check(!watcher.AtPrompt, "a newline ends the line: the prompt is behind");
    }

    private static void Sync()
    {
        System.Collections.Generic.List<string> typed = new System.Collections.Generic.List<string>();
        DateTime clock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        SmarTTY.Terminal.TerminalDirectoryWatcher watcher = new SmarTTY.Terminal.TerminalDirectoryWatcher();
        SmarTTY.Terminal.TerminalDirectorySync sync = new SmarTTY.Terminal.TerminalDirectorySync(watcher, typed.Add, () => clock);

        sync.Request("/D:/data");
        Test.Check(typed.Count == 0, "nothing is typed while the shell is not known to wait at a prompt");
        watcher.Observe("C:\\Users\\me>");
        Test.Equal("cd /d \"D:\\data\"\r", string.Join("|", typed), "the request that waited is typed when the cmd.exe prompt comes");

        // The shell has done it and shows the new prompt: the next request is typed again.
        typed.Clear();
        watcher.Observe("cd /d \"D:\\data\"\r\n\r\nD:\\data>");
        sync.Request("/D:/data");
        Test.Check(typed.Count == 0, "a request for the directory the shell is in is not typed");
        sync.Request("/D:/DATA/");
        Test.Check(typed.Count == 0, "also in other letters and with a trailing slash");
        sync.Request("/E:/x y");
        Test.Equal("cd /d \"E:\\x y\"\r", string.Join("|", typed), "a path with a space is in quotes");

        // A line being typed is not interrupted; the newest request wins when the prompt is back.
        typed.Clear();
        watcher.Observe("\r\nE:\\x y>");
        clock = clock.AddSeconds(1);
        watcher.Observe("di");
        sync.Request("/C:/one");
        sync.Request("/C:/two");
        Test.Check(typed.Count == 0, "nothing is typed into a line the user is typing");
        watcher.Observe("r\r\n\r\nE:\\x y>");
        Test.Equal("cd /d \"C:\\two\"\r", string.Join("|", typed), "when the prompt is back, the newest request is typed, once");

        // One command is on its way: a second request waits for its prompt, and a lost answer does not block for ever.
        typed.Clear();
        sync.Request("/C:/three");
        Test.Check(typed.Count == 0, "while the previous command has not been answered, the next one waits");
        watcher.Observe("cd /d \"C:\\two\"\r\n\r\nC:\\two>");
        Test.Equal("cd /d \"C:\\three\"\r", string.Join("|", typed), "and is typed at the prompt that answers it");
        typed.Clear();
        clock = clock.AddSeconds(3);
        sync.Request("/C:/four");
        Test.Check(typed.Count == 0, "still waiting for the prompt after the command that was typed");
        clock = clock.AddSeconds(30);
        sync.Request("/C:/four");
        Test.Equal("cd /d \"C:\\four\"\r", string.Join("|", typed), "a command that got no answer for a long time is forgotten, and the next request goes out");

        // PowerShell, and the paths that cannot be passed.
        typed.Clear();
        SmarTTY.Terminal.TerminalDirectoryWatcher psWatcher = new SmarTTY.Terminal.TerminalDirectoryWatcher();
        SmarTTY.Terminal.TerminalDirectorySync psSync = new SmarTTY.Terminal.TerminalDirectorySync(psWatcher, typed.Add, () => clock);
        psWatcher.Observe("PS C:\\Users\\me> ");
        psSync.Request("/D:/My [files]/O'Neil");
        Test.Equal("Set-Location -LiteralPath 'D:\\My [files]\\O''Neil'\r", string.Join("|", typed), "PowerShell gets Set-Location -LiteralPath, with the quote doubled");
        typed.Clear();
        psWatcher.Observe("\r\nPS D:\\My [files]\\O'Neil> ");
        psSync.Request("/");
        psSync.Request("/C:/100%/x");
        Test.Check(typed.Count == 0, "the place above the drives and a path with % are not typed");

        Test.Equal("Set-Location -LiteralPath 'C:\\a\u2019\u2019b'", SmarTTY.Terminal.TerminalDirectorySync.BuildCommand(SmarTTY.Terminal.PromptShell.PowerShell, "C:\\a\u2019b"), "a typographic quote is doubled for PowerShell as well");
        Test.Equal("cd /d \"C:\\a&b^c\"", SmarTTY.Terminal.TerminalDirectorySync.BuildCommand(SmarTTY.Terminal.PromptShell.Cmd, "C:\\a&b^c"), "& and ^ are safe inside the quotes of cmd.exe");
        Test.Check(SmarTTY.Terminal.TerminalDirectorySync.BuildCommand(SmarTTY.Terminal.PromptShell.Cmd, "C:\\a\"b") == null && SmarTTY.Terminal.TerminalDirectorySync.BuildCommand(SmarTTY.Terminal.PromptShell.Unknown, "C:\\a") == null && SmarTTY.Terminal.TerminalDirectorySync.BuildCommand(SmarTTY.Terminal.PromptShell.Cmd, "C:\\a%PATH%") == null, "a quote, a % and an unknown shell give no command");
    }

    private static void Certutil()
    {
        string hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        Test.Equal("SHA-256 " + hash, Format(Transport.WindowsRemoteFileTools.ParseChecksum(new[] { "SHA256 hash of C:\\x\\file.bin:", hash, "CertUtil: -hashfile command completed successfully." })), "the hash line of certutil is found among the words");
        Test.Equal("SHA-256 " + hash, Format(Transport.WindowsRemoteFileTools.ParseChecksum(new[] { "SHA256-Hash von Datei C:\\x:", hash.ToUpperInvariant(), "CertUtil: Der Befehl -hashfile wurde erfolgreich ausgef\u00fchrt." })), "also in another language, with capital letters");
        Test.Equal("SHA-256 " + hash, Format(Transport.WindowsRemoteFileTools.ParseChecksum(new[] { "SHA256 hash of file:", string.Join(" ", Enumerable.Range(0, 32).Select(index => hash.Substring(index * 2, 2))), "CertUtil: -hashfile command completed successfully." })), "the older output with a space between every two digits");
        Test.Check(Transport.WindowsRemoteFileTools.ParseChecksum(new[] { "CertUtil: -hashfile command failed: 0x80070002 (WIN32: 2 ERROR_FILE_NOT_FOUND)", "The system cannot find the file specified." }) == null && Transport.WindowsRemoteFileTools.ParseChecksum(null) == null, "an error is no checksum");
        Test.Check(Transport.WindowsRemoteFileTools.ParseChecksum(new[] { hash.Substring(1) }) == null && Transport.WindowsRemoteFileTools.ParseChecksum(new[] { hash + "00" }) == null, "a hash of the wrong length is not one");
        Test.Equal("certutil -hashfile \"C:\\Users\\me\\a b.txt\" SHA256", Transport.WindowsRemoteFileTools.ChecksumCommand("/C:/Users/me/a b.txt"), "the certutil command, the path in double quotes");
        Test.Check(Transport.WindowsRemoteFileTools.ChecksumCommand("/home/user/x") == null, "no command for a path without a drive");
    }

    private static string Format(Transport.FileChecksum checksum)
    {
        return checksum == null ? null : checksum.Algorithm + " " + checksum.Value;
    }

    private static void Lines()
    {
        Test.Equal("a|b|", string.Join("|", WindowsAuthorizedKeys.SplitLines(System.Text.Encoding.UTF8.GetBytes("a\r\nb\r\n\r\n"))), "CRLF lines, the empty line before the end is kept and the end is not a line");
        Test.Equal("a|b", string.Join("|", WindowsAuthorizedKeys.SplitLines(System.Text.Encoding.UTF8.GetBytes("a\nb"))), "LF lines without a newline at the end");
        Test.Check(WindowsAuthorizedKeys.SplitLines(new byte[0]).Count == 0 && WindowsAuthorizedKeys.SplitLines(null).Count == 0, "an empty file has no lines");
    }
}
