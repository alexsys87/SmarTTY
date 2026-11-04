using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// The "ls" of the file and folder dialogs answered over SFTP for a Windows host (<see cref="SftpListingCommand"/>): the three
/// forms the dialogs use, links shown as what they point to, the exit code and the words of a failure that the dialogs read.
/// </summary>
internal static class SftpListingTests
{
    public static void Run(TestEnvironment environment)
    {
        Test.Section("ls for the file dialogs of a Windows host (SFTP)");
        SshSession session = new SshSession(environment.Parameters());
        string directory = environment.RemoteHome + "/smartty-winls-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!session.Authenticate(environment.User, environment.Password))
            {
                throw new InvalidOperationException("Could not authenticate against the test server.");
            }
            session.RemoteOs = RemoteOsKind.Windows;
            RunChecks(session, directory, (target, link) => session.RunCommand("ln -s '" + target + "' '" + link + "'", out _));
        }
        finally
        {
            try
            {
                using SftpChannel sftp = SftpChannel.Open(session);
                new SftpFileSystem(sftp).Delete(directory);
            }
            catch
            {
            }
            session.Dispose();
        }
    }

    private static IRemoteCommand Ls(SshSession session, string arguments)
    {
        IRemoteCommand command = new SftpListingCommand(session, arguments);
        Test.Check(command.WaitForExit(60000), "ls " + arguments + " finishes");
        return command;
    }

    private static string Lines(IRemoteCommand command)
    {
        return string.Join("|", command.OutputLines.OrderBy(line => line, StringComparer.Ordinal));
    }

    internal static void RunChecks(SshSession session, string directory, Action<string, string> makeLink)
    {
        string cyrillic = "\u0444\u0430\u0439\u043b \u0441 \u043f\u0440\u043e\u0431\u0435\u043b\u043e\u043c.txt";
        using (SftpChannel sftp = SftpChannel.Open(session))
        {
            SftpFileSystem fileSystem = new SftpFileSystem(sftp);
            fileSystem.CreateDirectories(directory + "/sub");
            foreach (string name in new[] { "file.txt", ".hidden", cyrillic })
            {
                using Stream stream = sftp.OpenWrite(directory + "/" + name, 0, truncate: true);
                stream.WriteByte(65);
            }
        }
        makeLink(directory + "/sub", directory + "/link-dir");
        makeLink(directory + "/file.txt", directory + "/link-file");
        string expected = ".hidden|file.txt|link-dir/|link-file|sub/|" + cyrillic;

        foreach (string flags in new[] { "-1ALF", "-1pLA", "-F" })
        {
            using IRemoteCommand listing = Ls(session, flags + " \"" + directory + "\"");
            Test.Check(listing.ExitCode == 0 && string.IsNullOrEmpty(listing.ErrorText), "ls " + flags + " of a directory succeeds");
            Test.Equal(expected, Lines(listing), "ls " + flags + ": names, a folder with a slash, a link as what it points to (-L), Cyrillic and spaces");
        }
        using (IRemoteCommand listing = Ls(session, "-1ALF \"" + directory + "\""))
        {
            listing.OutputLines.Clear();
            Test.Equal(6, listing.OutputLines.Count, "the lines are a copy: a caller that sorts or removes lines does not change the answer");
        }

        using (IRemoteCommand folder = Ls(session, "-1dLF \"" + directory + "/sub\""))
        {
            Test.Check(folder.ExitCode == 0 && Lines(folder) == directory + "/sub/", "ls -d of a folder: the path with a slash (the dialog tells a folder by it)");
        }
        using (IRemoteCommand file = Ls(session, "-1dLF \"" + directory + "/file.txt\""))
        {
            Test.Check(file.ExitCode == 0 && Lines(file) == directory + "/file.txt", "ls -d of a file: the path without a slash");
        }
        using (IRemoteCommand folderLink = Ls(session, "-1dLF \"" + directory + "/link-dir\""))
        {
            Test.Check(Lines(folderLink) == directory + "/link-dir/", "ls -dL of a link to a folder is a folder");
        }

        using (IRemoteCommand missing = Ls(session, "-1dLF \"" + directory + "/nothing\""))
        {
            Test.Check(missing.ExitCode == 2 && missing.OutputLines.Count == 0, "a path that is not there: exit code 2 and no lines");
            Test.Check(missing.ErrorText != null && missing.ErrorText.StartsWith("ls: cannot access '" + directory + "/nothing'") && missing.ErrorText.EndsWith("No such file or directory"), "and the words of ls, which the dialog shows");
        }
        using (IRemoteCommand missingFolder = Ls(session, "-1pLA \"" + directory + "/nothing\""))
        {
            Test.Check(missingFolder.ExitCode == 2 && missingFolder.ErrorText.Contains("No such file or directory"), "listing a directory that is not there fails the same way");
        }
        using (IRemoteCommand notAFolder = Ls(session, "-1ALF \"" + directory + "/file.txt\""))
        {
            Test.Check(notAFolder.ExitCode != 0 && !string.IsNullOrEmpty(notAFolder.ErrorText), "listing a file as a directory fails with a message");
        }
        using (IRemoteCommand root = Ls(session, "-1pLA \"/\""))
        {
            Test.Check(root.ExitCode == 0 && root.OutputLines.Count > 0 && root.OutputLines.All(line => line.Length > 0), "\"/\" can be listed (the drives of a Windows host; here a Unix root, which has no drives)");
        }
        using (IRemoteCommand garbage = Ls(session, "this is not what the dialogs send"))
        {
            Test.Check(garbage.ExitCode == 2 && garbage.ErrorText != null && garbage.ErrorText.Contains("not understood"), "arguments that are not understood end with an error, not an exception");
        }
        IRemoteCommand disposed = Ls(session, "-1dLF \"" + directory + "\"");
        disposed.Dispose();
        disposed.Dispose();
        Test.Check(disposed.ExitCode == 0, "a command can be disposed twice and read afterwards");
        IRemoteCommand early = new SftpListingCommand(session, "-1ALF \"" + directory + "\"");
        early.Dispose();
        System.Threading.Thread.Sleep(300);
        Test.Check(true, "a command that is disposed before it has finished does not break the thread that finishes it");
        Test.Check(!string.IsNullOrEmpty(Lines(Ls(session, "-1ALF \"" + directory + "\""))), "the session is usable afterwards");
    }
}
