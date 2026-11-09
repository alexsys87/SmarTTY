using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SmarTTY.Ssh;

// Replacing the key file of the administrators of a Windows host (Ssh/KeyFileReplacer.cs): a connection that is lost, a failure of icacls
// or of a rename must never leave the server without a key file that works, and the old keys must never be lost. The steps are run on an
// in-memory file system that can fail at any operation, and the extension requests are spoken to a real sftp-server of OpenSSH.
// Run with: dotnet run --project tests/KeyFileReplaceTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static byte[] B(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    private static string S(byte[] bytes)
    {
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Files in memory, with the rules of SFTP version 3 (a rename refuses an existing target) and faults: the connection can be lost at
    /// the n-th operation (that one and every later one throw IOException), or one kind of operation can fail with an error of the server.
    /// </summary>
    private sealed class FakeFiles : IReplaceableFiles
    {
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();

        public bool Atomic;

        public int Operations;

        public int LoseConnectionAt = -1;

        public string FailOperation;

        public string FailFor;

        public long? WriteOnlyBytes;

        public readonly List<string> Log = new List<string>();

        public Action<string> AfterOperation;

        public bool SupportsAtomicReplace => Atomic;

        private void Step(string operation, string path)
        {
            Operations++;
            Log.Add(operation + " " + path);
            if (LoseConnectionAt >= 0 && Operations >= LoseConnectionAt)
            {
                throw new IOException("The connection was lost.");
            }
            if (FailOperation == operation && (FailFor == null || path.EndsWith(FailFor)))
            {
                throw new RemoteOperationException(operation + " refused for " + path);
            }
        }

        private void Done(string operation)
        {
            AfterOperation?.Invoke(operation);
        }

        public void Write(string path, byte[] content)
        {
            Step("write", path);
            Files[path] = WriteOnlyBytes.HasValue ? content.Take((int)WriteOnlyBytes.Value).ToArray() : content.ToArray();
            Done("write");
        }

        public long? SizeOf(string path)
        {
            Step("size", path);
            return Files.TryGetValue(path, out byte[] data) ? data.Length : (long?)null;
        }

        public bool Exists(string path)
        {
            Step("exists", path);
            return Files.ContainsKey(path);
        }

        public void Remove(string path)
        {
            Step("remove", path);
            if (!Files.Remove(path))
            {
                throw new RemoteOperationException("No such file: " + path);
            }
            Done("remove");
        }

        public void Rename(string from, string to)
        {
            Step("rename", from + " " + to);
            if (!Files.ContainsKey(from))
            {
                throw new RemoteOperationException("No such file: " + from);
            }
            if (Files.ContainsKey(to))
            {
                throw new RemoteOperationException("Failure: " + to + " exists");
            }
            Files[to] = Files[from];
            Files.Remove(from);
            Done("rename");
        }

        public void ReplaceByRename(string from, string to)
        {
            Step("posix-rename", from + " " + to);
            if (!Files.ContainsKey(from))
            {
                throw new RemoteOperationException("No such file: " + from);
            }
            Files[to] = Files[from];
            Files.Remove(from);
            Done("posix-rename");
        }
    }

    private const string Path = "/C:/ProgramData/ssh/administrators_authorized_keys";
    private const string Temp = Path + ".smartty";
    private const string Backup = Path + ".smartty-old";

    private static FakeFiles WithKeys(bool atomic, string content = "old keys\n")
    {
        FakeFiles files = new FakeFiles { Atomic = atomic };
        files.Files[Path] = B(content);
        return files;
    }

    private static void HappyPaths()
    {
        foreach (bool atomic in new[] { true, false })
        {
            string label = atomic ? "posix-rename" : "two renames";
            FakeFiles files = WithKeys(atomic);
            List<string> states = new List<string>();
            bool missing = false;
            bool oldLost = false;
            files.AfterOperation = operation =>
            {
                // After every step that changed something: is there a key file, or at least the old keys somewhere?
                if (!files.Files.ContainsKey(Path))
                {
                    missing = true;
                }
                if (!files.Files.Values.Any(data => S(data) == "old keys\n" || S(data) == "old keys\nnew key\n"))
                {
                    oldLost = true;
                }
            };
            KeyFileReplacer.Replace(files, Path, B("old keys\nnew key\n"), null);
            Check(S(files.Files[Path]) == "old keys\nnew key\n" && files.Files.Count == 1, label + ": the new content is in place and no temporary file or copy is left");
            Check(!oldLost, label + ": the old keys were somewhere at every moment");
            if (atomic)
            {
                Check(!missing, label + ": there was no moment without a key file");
                Check(files.Log.Any(line => line.StartsWith("posix-rename")) && !files.Log.Any(line => line.StartsWith("remove " + Path + " ")), label + ": the old file was never removed");
            }
            else
            {
                Check(files.Log.Any(line => line == "rename " + Path + " " + Backup) && files.Log.Any(line => line == "rename " + Temp + " " + Path), label + ": the old file is moved aside, not removed, before the new one is put in place");
                Check(!files.Log.Contains("remove " + Path), label + ": the old file is never removed (only its copy, when the new one is in place)");
            }

            files = new FakeFiles { Atomic = atomic };
            KeyFileReplacer.Replace(files, Path, B("first\n"), null);
            Check(S(files.Files[Path]) == "first\n" && files.Files.Count == 1, label + ": a file that did not exist is created with its first content");
        }
    }

    private static void PrepareStep()
    {
        foreach (bool atomic in new[] { true, false })
        {
            string label = atomic ? "posix-rename" : "two renames";
            FakeFiles files = WithKeys(atomic);
            string seenPath = null;
            string oldAtThatTime = null;
            bool newFileThere = false;
            KeyFileReplacer.Replace(files, Path, B("new\n"), prepared =>
            {
                seenPath = prepared;
                oldAtThatTime = S(files.Files.ContainsKey(Path) ? files.Files[Path] : null);
                newFileThere = files.Files.ContainsKey(prepared) && S(files.Files[prepared]) == "new\n";
            });
            Check(seenPath == Temp && newFileThere && oldAtThatTime == "old keys\n", label + ": the work on the new file (icacls) is done on the temporary file, complete, while the old file is still the key file");
            Check(S(files.Files[Path]) == "new\n", label + ": and then the new file takes the name");

            files = new FakeFiles { Atomic = atomic };
            string beforeFirst = null;
            KeyFileReplacer.Replace(files, Path, B("first\n"), prepared => beforeFirst = files.Files.ContainsKey(Path) ? "named" : "not yet");
            Check(beforeFirst == "not yet", label + ": for a file that is new too, the access is closed before it has the name sshd looks for");

            files = WithKeys(atomic);
            try
            {
                KeyFileReplacer.Replace(files, Path, B("new\n"), prepared => throw new RemoteOperationException("Cannot restrict the access to " + prepared + " (icacls failed)"));
                throw new Exception("FAIL: icacls failed and nothing was thrown");
            }
            catch (RemoteOperationException exception)
            {
                Check(exception.Message.Contains("icacls failed"), label + ": a failure of icacls is reported as it is");
            }
            Check(files.Files.Count == 1 && S(files.Files[Path]) == "old keys\n", label + ": and the key file is as it was, with no temporary file left (it used to stay without usable rights)");

            files = WithKeys(atomic);
            Check(Throws<IOException>(() => KeyFileReplacer.Replace(files, Path, B("new\n"), prepared => throw new IOException("lost"))) && S(files.Files[Path]) == "old keys\n", label + ": a connection lost during icacls leaves the old file in place");
        }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return true;
        }
        return false;
    }

    private static void Failures()
    {
        FakeFiles files = WithKeys(true);
        files.WriteOnlyBytes = 3;
        try
        {
            KeyFileReplacer.Replace(files, Path, B("old keys\nnew\n"), null);
            throw new Exception("FAIL: a short write was not reported");
        }
        catch (RemoteOperationException exception)
        {
            Check(exception.Message.Contains("Only 3 of 13 bytes") && exception.Message.Contains("was not changed"), "a write that is cut short is reported");
        }
        Check(files.Files.Count == 1 && S(files.Files[Path]) == "old keys\n", "and leaves the old file alone, with no half file beside it");

        files = WithKeys(true);
        files.FailOperation = "posix-rename";
        KeyFileReplacer.Replace(files, Path, B("new\n"), null);
        Check(S(files.Files[Path]) == "new\n" && files.Files.Count == 1, "a server that announces posix-rename and refuses it: the two steps do the work");

        files = WithKeys(false);
        files.FailOperation = "rename";
        files.FailFor = Backup;
        try
        {
            KeyFileReplacer.Replace(files, Path, B("new\n"), null);
            throw new Exception("FAIL: moving the old file aside failed and nothing was thrown");
        }
        catch (RemoteOperationException exception)
        {
            Check(exception.Message.Contains("Cannot move the old") && exception.Message.Contains("not changed"), "the old file cannot be moved aside: reported");
        }
        Check(files.Files.Count == 1 && S(files.Files[Path]) == "old keys\n", "and it is where it was, the temporary file is gone");

        files = WithKeys(false);
        files.FailOperation = "rename";
        files.FailFor = Temp + " " + Path;
        try
        {
            KeyFileReplacer.Replace(files, Path, B("new\n"), null);
            throw new Exception("FAIL: putting the new file in place failed and nothing was thrown");
        }
        catch (RemoteOperationException exception)
        {
            Check(exception.Message.Contains("The old content is back in place") && exception.Message.Contains(Temp), "the new file cannot take the name: the old one is put back, and the message says where the new content is");
        }
        Check(S(files.Files[Path]) == "old keys\n" && S(files.Files[Temp]) == "new\n" && !files.Files.ContainsKey(Backup), "so the server has its old key file, and the new content is kept beside it");

        // The refusal is everything: neither the new file nor the old one can be renamed back.
        files = WithKeys(false);
        files.FailOperation = "rename";
        files.FailFor = "\u0001never";
        FakeFiles broken = files;
        broken.AfterOperation = operation =>
        {
            if (operation == "rename" && broken.Files.ContainsKey(Backup))
            {
                broken.FailFor = Path; // from now on every rename that names the key file fails
            }
        };
        try
        {
            KeyFileReplacer.Replace(files, Path, B("new\n"), null);
            throw new Exception("FAIL: nothing was thrown when no rename of the key file worked");
        }
        catch (RemoteOperationException exception)
        {
            Check(exception.Message.Contains("The old content is in " + Backup) && !exception.Message.Contains("back in place"), "when the old file cannot be put back, the message names its copy");
        }
        Check(files.Files.Values.Any(data => S(data) == "old keys\n"), "and the old keys still exist somewhere on the server");
    }

    private static void ConnectionLostEverywhere()
    {
        foreach (bool atomic in new[] { true, false })
        {
            string label = atomic ? "posix-rename" : "two renames";
            // How many operations does a replacement take?
            FakeFiles reference = WithKeys(atomic);
            KeyFileReplacer.Replace(reference, Path, B("old keys\nnew key\n"), prepared => { });
            int total = reference.Operations;
            int withoutKeyFile = 0;
            int recoveredCount = 0;
            for (int lostAt = 1; lostAt <= total; lostAt++)
            {
                FakeFiles files = WithKeys(atomic);
                files.LoseConnectionAt = lostAt;
                try
                {
                    KeyFileReplacer.Replace(files, Path, B("old keys\nnew key\n"), prepared => { });
                }
                catch (IOException)
                {
                }
                if (!files.Files.Values.Any(data => S(data) == "old keys\n" || S(data) == "old keys\nnew key\n"))
                {
                    throw new Exception("FAIL: " + label + ": the connection lost at operation " + lostAt + " lost the keys: " + string.Join(", ", files.Files.Keys));
                }
                if (!files.Files.ContainsKey(Path))
                {
                    withoutKeyFile++;
                }

                // The next run, on a connection that works, finds the file where it belongs before it reads it.
                files.LoseConnectionAt = -1;
                files.Operations = 0;
                if (KeyFileReplacer.RecoverInterrupted(files, Path))
                {
                    recoveredCount++;
                }
                if (!files.Files.ContainsKey(Path) || (S(files.Files[Path]) != "old keys\n" && S(files.Files[Path]) != "old keys\nnew key\n"))
                {
                    throw new Exception("FAIL: " + label + ": after a connection lost at operation " + lostAt + " the next run did not find the keys");
                }
                KeyFileReplacer.Replace(files, Path, B("old keys\nnew key\n"), prepared => { });
                if (S(files.Files[Path]) != "old keys\nnew key\n" || files.Files.Count != 1)
                {
                    throw new Exception("FAIL: " + label + ": after a connection lost at operation " + lostAt + " the next run did not finish cleanly: " + string.Join(", ", files.Files.Keys));
                }
            }
            Check(true, label + ": a connection lost at any of the " + total + " operations loses no key, and the next run finishes the job and leaves no file beside the key file");
            if (atomic)
            {
                Check(withoutKeyFile == 0, label + ": there is never a moment without the key file, whatever the connection does");
            }
            else
            {
                Check(withoutKeyFile == 1 && recoveredCount == 1, label + ": only between the two renames the key file is missing (" + withoutKeyFile + " of " + total + " operations), and the next run puts it back");
            }
        }
    }

    private static void Recovery()
    {
        FakeFiles files = new FakeFiles();
        files.Files[Backup] = B("old keys\n");
        Check(KeyFileReplacer.RecoverInterrupted(files, Path) && S(files.Files[Path]) == "old keys\n" && !files.Files.ContainsKey(Backup), "recovery: the file is missing and its copy is there: the copy takes the name");
        Check(!KeyFileReplacer.RecoverInterrupted(files, Path), "recovery: and nothing more to do");
        files = WithKeys(false);
        files.Files[Backup] = B("an older copy\n");
        Check(!KeyFileReplacer.RecoverInterrupted(files, Path) && S(files.Files[Path]) == "old keys\n" && S(files.Files[Backup]) == "an older copy\n", "recovery: a copy beside an existing key file is not used, and not touched");
        Check(!KeyFileReplacer.RecoverInterrupted(new FakeFiles(), Path), "recovery: no file and no copy is nothing to recover");

        // The copy of a replacement that had succeeded but could not delete it is in the way of the next one.
        files = WithKeys(false);
        files.Files[Backup] = B("stale\n");
        KeyFileReplacer.Replace(files, Path, B("new\n"), null);
        Check(S(files.Files[Path]) == "new\n" && files.Files.Count == 1, "a stale copy beside an existing key file does not stop the next replacement, and is gone after it");

        // A temporary file of an earlier attempt is written over.
        files = WithKeys(true);
        files.Files[Temp] = B("junk from an attempt before\n");
        KeyFileReplacer.Replace(files, Path, B("new\n"), null);
        Check(S(files.Files[Path]) == "new\n" && files.Files.Count == 1, "a temporary file of an earlier attempt is written over");

        // The copy cannot be removed at the end: the job is done anyway.
        files = WithKeys(false);
        files.FailOperation = "remove";
        files.FailFor = Backup;
        KeyFileReplacer.Replace(files, Path, B("new\n"), null);
        Check(S(files.Files[Path]) == "new\n", "when the copy cannot be removed afterwards, the new file is in place and nothing is reported");
    }

    private static void Extensions()
    {
        List<byte> packet = new List<byte> { 2, 0, 0, 0, 3 };
        void Add(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            packet.AddRange(new byte[] { 0, 0, (byte)(data.Length >> 8), (byte)data.Length });
            packet.AddRange(data);
        }
        Add("posix-rename@openssh.com");
        Add("1");
        Add("hardlink@openssh.com");
        Add("1");
        Dictionary<string, string> parsed = SftpExtensions.ParseVersionExtensions(packet.ToArray());
        Check(parsed.Count == 2 && parsed["posix-rename@openssh.com"] == "1" && parsed.ContainsKey("hardlink@openssh.com"), "extensions: the pairs after the version are read");
        Check(SftpExtensions.ParseVersionExtensions(new byte[] { 2, 0, 0, 0, 3 }).Count == 0, "extensions: a version reply with none has none");
        Check(SftpExtensions.ParseVersionExtensions(null).Count == 0 && SftpExtensions.ParseVersionExtensions(new byte[0]).Count == 0, "extensions: nothing is nothing");
        byte[] cut = packet.Take(packet.Count - 3).ToArray();
        Check(SftpExtensions.ParseVersionExtensions(cut).Count == 1, "extensions: a list that is cut off gives the pairs before the cut");
        byte[] huge = { 2, 0, 0, 0, 3, 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3 };
        Check(SftpExtensions.ParseVersionExtensions(huge).Count == 0, "extensions: a length that is larger than the packet is not followed");
        byte[] request = SftpExtensions.BuildExtendedRequest(0x01020304, "posix-rename@openssh.com", "/a", "/bb");
        byte[] expected = new byte[] { 0, 0, 0, 1 + 4 + 4 + 24 + 4 + 2 + 4 + 3, 200, 1, 2, 3, 4, 0, 0, 0, 24 }
            .Concat(Encoding.ASCII.GetBytes("posix-rename@openssh.com")).Concat(new byte[] { 0, 0, 0, 2 }).Concat(Encoding.ASCII.GetBytes("/a")).Concat(new byte[] { 0, 0, 0, 3 }).Concat(Encoding.ASCII.GetBytes("/bb")).ToArray();
        Check(request.SequenceEqual(expected), "extensions: the request is the length, 200, the id and three strings");
        Check(Encoding.UTF8.GetString(SftpExtensions.BuildExtendedRequest(1, "x", "\u00e4\u00f6").Skip(4 + 1 + 4 + 4 + 1 + 4).ToArray()) == "\u00e4\u00f6", "extensions: a path with letters outside ASCII is sent as UTF-8");
    }

    // ---- a real sftp-server -------------------------------------------------------------------------------------------------------

    private sealed class Server : IDisposable
    {
        private readonly Process _process;

        public Server(string binary)
        {
            _process = Process.Start(new ProcessStartInfo(binary) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
        }

        public void Send(byte[] packet)
        {
            _process.StandardInput.BaseStream.Write(packet, 0, packet.Length);
            _process.StandardInput.BaseStream.Flush();
        }

        public byte[] Receive()
        {
            Stream stream = _process.StandardOutput.BaseStream;
            byte[] header = ReadExactly(stream, 4);
            int length = header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3];
            return ReadExactly(stream, length);
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] data = new byte[count];
            int done = 0;
            while (done < count)
            {
                int read = stream.Read(data, done, count - done);
                if (read <= 0)
                {
                    throw new IOException("The server ended");
                }
                done += read;
            }
            return data;
        }

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(3000))
                {
                    _process.Kill();
                }
            }
            catch
            {
            }
        }
    }

    private static byte[] Initialize(Server server)
    {
        server.Send(new byte[] { 0, 0, 0, 5, 1, 0, 0, 0, 3 });
        return server.Receive();
    }

    // The status of the reply to the request with this id: (type, id, status).
    private static int StatusOf(byte[] reply, uint id)
    {
        uint replyId = (uint)(reply[1] << 24 | reply[2] << 16 | reply[3] << 8 | reply[4]);
        if (reply[0] != 101 || replyId != id)
        {
            throw new Exception("FAIL: an unexpected reply: type " + reply[0] + " id " + replyId);
        }
        return reply[5] << 24 | reply[6] << 16 | reply[7] << 8 | reply[8];
    }

    private static byte[] PlainRename(uint id, string from, string to)
    {
        List<byte> body = new List<byte> { 18, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id };
        foreach (string text in new[] { from, to })
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            body.AddRange(new byte[] { 0, 0, 0, (byte)data.Length });
            body.AddRange(data);
        }
        return new byte[] { 0, 0, 0, (byte)body.Count }.Concat(body).ToArray();
    }

    private static void RealServer()
    {
        string binary = new[] { "/usr/lib/openssh/sftp-server", "/usr/libexec/openssh/sftp-server", "/usr/libexec/sftp-server", "/usr/lib/ssh/sftp-server" }.FirstOrDefault(File.Exists);
        if (binary == null || OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: no sftp-server of OpenSSH here");
            return;
        }
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smartty-sftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string target = System.IO.Path.Combine(folder, "administrators_authorized_keys");
            string temporary = target + ".smartty";
            using (Server server = new Server(binary))
            {
                byte[] version = Initialize(server);
                Check(version[0] == 2, "sftp-server: it answers the handshake with a version packet");
                Dictionary<string, string> extensions = SftpExtensions.ParseVersionExtensions(version);
                Check(extensions.ContainsKey(SftpExtensions.PosixRename), "sftp-server: the extension is announced in the version reply, and the parser finds it (" + string.Join(", ", extensions.Keys) + ")");

                File.WriteAllText(target, "old keys\n");
                File.WriteAllText(temporary, "old keys\nnew key\n");
                server.Send(PlainRename(1, temporary, target));
                Check(StatusOf(server.Receive(), 1) == 4, "sftp-server: the plain rename over an existing file is refused (SFTP version 3), which is why posix-rename is used");
                Check(File.ReadAllText(target) == "old keys\n" && File.Exists(temporary), "sftp-server: and changed nothing");

                server.Send(SftpExtensions.BuildExtendedRequest(2, SftpExtensions.PosixRename, temporary, target));
                Check(StatusOf(server.Receive(), 2) == 0, "sftp-server: posix-rename over an existing file is accepted");
                Check(File.ReadAllText(target) == "old keys\nnew key\n" && !File.Exists(temporary), "sftp-server: the target has the new content at once and the temporary file is gone");

                server.Send(SftpExtensions.BuildExtendedRequest(3, SftpExtensions.PosixRename, temporary, target));
                Check(StatusOf(server.Receive(), 3) == 2 && File.ReadAllText(target) == "old keys\nnew key\n", "sftp-server: a source that is not there is 'no such file' and the target is not touched");

                string unicode = System.IO.Path.Combine(folder, "\u00e4\u00f6 keys");
                File.WriteAllText(System.IO.Path.Combine(folder, "from \u00fc"), "x");
                server.Send(SftpExtensions.BuildExtendedRequest(4, SftpExtensions.PosixRename, System.IO.Path.Combine(folder, "from \u00fc"), unicode));
                Check(StatusOf(server.Receive(), 4) == 0 && File.Exists(unicode), "sftp-server: paths with letters outside ASCII are understood");

                server.Send(SftpExtensions.BuildExtendedRequest(5, "no-such-extension@example.com", "a", "b"));
                Check(StatusOf(server.Receive(), 5) == 8, "sftp-server: an extension it does not know is 'not supported'");
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static int Main()
    {
        HappyPaths();
        PrepareStep();
        Failures();
        ConnectionLostEverywhere();
        Recovery();
        Extensions();
        RealServer();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
