using SmarTTY.Transport;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// What the remote file editor needs of a Windows host (<see cref="WindowsRemoteFileEditing"/>): reading a file, the version
/// signature that tells that somebody else changed it, and saving in place with an optional "file~" backup, over SFTP.
/// </summary>
internal static class WindowsEditingTests
{
    public static void Run(TestEnvironment environment)
    {
        Test.Section("Remote file editor on a Windows host (SFTP)");
        SshSession session = new SshSession(environment.Parameters());
        string directory = environment.RemoteHome + "/smartty-winedit-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!session.Authenticate(environment.User, environment.Password))
            {
                throw new InvalidOperationException("Could not authenticate against the test server.");
            }
            session.RemoteOs = RemoteOsKind.Windows;
            RunChecks(session, directory);
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

    private static byte[] Read(SshSession session, string path)
    {
        using Stream stream = WindowsRemoteFileEditing.OpenRead(session, path, out long length);
        using MemoryStream buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Test.Equal((long)buffer.Length, length, "the size that the stream announces is the number of bytes it gives");
        return buffer.ToArray();
    }

    internal static void RunChecks(SshSession session, string directory)
    {
        using (SftpChannel sftp = SftpChannel.Open(session))
        {
            new SftpFileSystem(sftp).CreateDirectory(directory);
        }
        string file = directory + "/config";

        Test.Check(!WindowsRemoteFileEditing.Exists(session, file) && WindowsRemoteFileEditing.Signature(session, file) == null, "a file that is not there: it does not exist and has no signature");

        // A new file: the exact bytes, whatever they are (a byte order mark, CRLF, bytes that are not UTF-8).
        byte[] exact = { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n', 0xFF, 0xFE, 0x00, 0x80, (byte)'z' };
        WindowsRemoteFileEditing.Save(session, file, exact, backup: false);
        Test.Check(WindowsRemoteFileEditing.Exists(session, file), "a new file is created");
        Test.Check(Read(session, file).SequenceEqual(exact), "with exactly the bytes that were saved (BOM, CRLF and invalid UTF-8 included)");
        string first = WindowsRemoteFileEditing.Signature(session, file);
        Test.Check(first != null && first.EndsWith(" " + exact.Length), "the signature ends with the size");

        // The same size, a second later: only the time tells.
        Thread.Sleep(1200);
        byte[] sameSize = exact.Select(value => (byte)(value ^ 1)).ToArray();
        WindowsRemoteFileEditing.Save(session, file, sameSize, backup: false);
        Test.Check(WindowsRemoteFileEditing.Signature(session, file) != first, "a change that keeps the size changes the signature (the time)");
        Test.Equal(WindowsRemoteFileEditing.Signature(session, file), WindowsRemoteFileEditing.Signature(session, file), "and an unchanged file has a stable signature");

        // A shorter version must not leave the end of the longer one behind.
        byte[] longer = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a long line of the older version\n", 50)));
        byte[] shorter = Encoding.UTF8.GetBytes("short\n");
        WindowsRemoteFileEditing.Save(session, file, longer, backup: false);
        WindowsRemoteFileEditing.Save(session, file, shorter, backup: false);
        Test.Check(Read(session, file).SequenceEqual(shorter), "a shorter version replaces a longer one completely");

        // The backup.
        WindowsRemoteFileEditing.Save(session, file, longer, backup: true);
        Test.Check(Read(session, file + "~").SequenceEqual(shorter) && Read(session, file).SequenceEqual(longer), "with a backup the old content is in file~");
        WindowsRemoteFileEditing.Save(session, file, shorter, backup: false);
        Test.Check(Read(session, file + "~").SequenceEqual(shorter), "a save without a backup leaves file~ as it was");
        string brand = directory + "/brand-new";
        WindowsRemoteFileEditing.Save(session, brand, longer, backup: true);
        Test.Check(!WindowsRemoteFileEditing.Exists(session, brand + "~") && Read(session, brand).SequenceEqual(longer), "a backup of a file that is not there is not made, and does not stop the save");
        WindowsRemoteFileEditing.Save(session, file, longer, backup: true);
        Test.Check(Read(session, file + "~").SequenceEqual(shorter), "an older file~ is replaced by the next backup");

        // Mistakes are reported, not hidden.
        Test.Throws<SftpStatusException>(() => WindowsRemoteFileEditing.Save(session, directory, shorter, backup: false), "saving to a path that is a directory fails");
        Test.Throws<SftpStatusException>(() => WindowsRemoteFileEditing.Save(session, directory + "/no-such-folder/x", shorter, backup: false), "saving into a folder that does not exist fails");
        Test.Throws<SftpStatusException>(() => WindowsRemoteFileEditing.OpenRead(session, directory + "/missing", out _), "opening a missing file fails with the status of the server");

        // A bigger file, read through the stream that owns its channel.
        byte[] big = new byte[3 * 1024 * 1024 + 5];
        new Random(5).NextBytes(big);
        WindowsRemoteFileEditing.Save(session, directory + "/big", big, backup: false);
        Test.Check(Read(session, directory + "/big").SequenceEqual(big), "a file of 3 MB is read completely");
        Stream stream = WindowsRemoteFileEditing.OpenRead(session, directory + "/big", out _);
        stream.Dispose();
        stream.Dispose();
        Test.Check(true, "a stream can be disposed twice");
        Test.Check(WindowsRemoteFileEditing.Exists(session, directory + "/big"), "and the session is usable after the channels of the streams are closed");
    }
}
