using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SmarTTY.IO;
using SmarTTY.Utilities;

// Regression harness for the command that saves a file of the remote editor (RemoteSaveCommand): it is run by real shells
// (/bin/sh, bash and BusyBox ash as on OpenWrt) on a real file system. Writing in place with cat used to leave the file cut off
// when the space ran out or the write failed half way; the checks are about the file that is left after that, and about what the
// command says. Needs a Linux with /bin/sh, df, cp, cat, wc and head; bash, BusyBox, setpriv and the right to mount a tmpfs
// (root) make more of the checks run, the rest is reported as skipped.
// Run with: dotnet run --project tests/RemoteSaveCommandTests
internal static class Program
{
    private static int _passed;

    private static int _skipped;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Skip(string reason)
    {
        _skipped++;
        Console.WriteLine("SKIP: " + reason);
    }

    private sealed class Shell
    {
        public string Name;

        public string[] Argv;

        /// <summary>False for BusyBox ash: it runs its own df and cat before it looks at the PATH, so stand-in tools do not work.</summary>
        public bool HonorsPath = true;
    }

    private sealed class Result
    {
        public int Exit;

        public List<string> Output = new List<string>();

        public string Text => string.Join("\n", Output);
    }

    private sealed class Options
    {
        public Dictionary<string, string> Env = new Dictionary<string, string>();

        public string PathPrefix;

        public string ExactPath;

        public int? LimitBlocks;

        public bool AsNobody;

        /// <summary>Hang the command up (SIGHUP to it and to its children) this long after it started, as a terminal does when the connection is lost.</summary>
        public int? HangupAfterMilliseconds;
    }

    private static readonly string BinSh = "/bin/sh";

    private static int Main()
    {
        if (!File.Exists(BinSh) || !File.Exists("/bin/cat"))
        {
            Console.WriteLine("SKIP: this needs a Linux with /bin/sh");
            return 0;
        }
        List<Shell> shells = new List<Shell> { new Shell { Name = "/bin/sh", Argv = new[] { BinSh, "-c" } } };
        string bash = new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists);
        if (bash != null)
        {
            shells.Add(new Shell { Name = "bash", Argv = new[] { bash, "-c" } });
        }
        else
        {
            Skip("bash is not installed");
        }
        string busybox = new[] { "/usr/bin/busybox", "/bin/busybox" }.FirstOrDefault(File.Exists);
        if (busybox != null)
        {
            shells.Add(new Shell { Name = "BusyBox ash", Argv = new[] { busybox, "ash", "-c" }, HonorsPath = false });
        }
        else
        {
            Skip("busybox is not installed: the command is not run with the tools of OpenWrt");
        }

        PureTests();
        foreach (Shell shell in shells)
        {
            Console.WriteLine();
            Console.WriteLine("== " + shell.Name);
            RealFileTests(shell);
            if (shell.HonorsPath)
            {
                StandInTests(shell);
            }
        }
        Console.WriteLine();
        Console.WriteLine("== a real file system that runs out of space");
        SmallFileSystemTests(shells);
        Console.WriteLine();
        Console.WriteLine("== without cmp, through the login shell wrapper");
        WithoutCmpTests();
        LoginShellTests();

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed" + (_skipped > 0 ? ", " + _skipped + " skipped" : "") + ".");
        return 0;
    }

    // ---------------------------------------------------------------- what needs no shell

    private static void PureTests()
    {
        Console.WriteLine("== the script and its messages");
        string script = RemoteSaveCommand.BuildScript("/etc/config/network", "/tmp/u.txt", 123, backup: false);
        Check(!script.Contains("\n") && !script.Contains("\r"), "script: one line (the sudo window shows it in a terminal)");
        Check(script.Contains("trap '' HUP PIPE"), "script: it ignores the loss of the connection and finishes");
        foreach (string tool in new[] { "awk", "sed", "tail", "dirname", "stat", "mktemp", "mv ", "sync" })
        {
            Check(!script.Contains(tool), "script: no " + tool.Trim() + " (OpenWrt may not have it)");
        }
        Check(script.Contains("'/etc/config/network'") && script.Contains("'/tmp/u.txt'") && script.Contains("'/tmp/u.txt.orig'"), "script: the three paths are quoted");
        Check(!script.Contains("cp -p"), "script: no backup unless asked");
        Check(RemoteSaveCommand.BuildScript("/etc/x", "/tmp/u.txt", 1, backup: true).Contains("cp -p \"$t\" '/etc/x~'"), "script: the backup copy of the old file when asked");
        Check(RemoteSaveCommand.BuildScript("/proc/sys/net/ipv4/ip_forward", "/tmp/u.txt", 1, false) == "cat '/tmp/u.txt' > '/proc/sys/net/ipv4/ip_forward'", "script: a file of /proc is just written, nothing is read back");
        Check(RemoteSaveCommand.BuildScript("/sys/class/leds/x/brightness", "/tmp/u.txt", 1, false).StartsWith("cat "), "script: and one of /sys");
        Check(RemoteSaveCommand.BuildScript("/dev/null", "/tmp/u.txt", 1, true) == "[ ! -e '/dev/null' ] || cp -p '/dev/null' '/dev/null~'; cat '/tmp/u.txt' > '/dev/null'", "script: and a device, with the backup it asked for");
        Check(RemoteSaveCommand.IsVerifiable("/etc/config/network") && RemoteSaveCommand.IsVerifiable("/tmp/procfile") && RemoteSaveCommand.IsVerifiable("/home/u/sys/x") && !RemoteSaveCommand.IsVerifiable("/proc/cpuinfo"), "script: only what lives under /proc, /sys and /dev is left out");
        Check(RemoteSaveCommand.RestoreCopyPath("/tmp/u.txt") == "/tmp/u.txt.orig", "script: the restore copy is next to the uploaded text");

        Check(RemoteSaveCommand.Describe(1, null, "/etc/x", "/tmp/u.txt") == null && RemoteSaveCommand.Describe(2, new[] { "sh: can't create" }, "/etc/x", "/tmp/u.txt") == null && RemoteSaveCommand.Describe(127, null, "/etc/x", "/tmp/u.txt") == null, "message: an ordinary failure (no permission) is not described, sudo may be offered");
        string noSpace = RemoteSaveCommand.Describe(RemoteSaveCommand.ExitNoSpace, new[] { "smartty-nospace 49000 10240" }, "/etc/x", "/tmp/u.txt");
        Check(noSpace.Contains("not enough free space") && noSpace.Contains("/etc/x") && noSpace.Contains("more are needed") && noSpace.Contains("47KB") && noSpace.Contains("10240 bytes") && noSpace.Contains("was not changed"), "message: no space names the file and the sizes");
        Check(!RemoteSaveCommand.Describe(RemoteSaveCommand.ExitNoSpace, null, "/etc/x", "/tmp/u.txt").Contains("("), "message: no space without the output (sudo) has no sizes");
        string restored = RemoteSaveCommand.Describe(RemoteSaveCommand.ExitRestored, new[] { "cat: write error: No space left on device", "smartty-restored" }, "/etc/x", "/tmp/u.txt");
        Check(restored.Contains("could not be written completely (cat: write error: No space left on device)") && restored.Contains("previous content has been put back") && !restored.Contains("smartty"), "message: a restored file says why and that it is back");
        string removed = RemoteSaveCommand.Describe(RemoteSaveCommand.ExitRestored, new[] { "smartty-removed" }, "/etc/new", "/tmp/u.txt");
        Check(removed.Contains("incomplete file has been removed") && !removed.Contains("("), "message: a new file that failed is removed");
        string kept = RemoteSaveCommand.Describe(RemoteSaveCommand.ExitNotRestored, new[] { "cat: x: I/O error", "smartty-kept /tmp/u.txt.orig" }, "/etc/x", "/tmp/u.txt");
        Check(kept.Contains("could not be put back") && kept.Contains("/tmp/u.txt.orig") && kept.Contains("/tmp/u.txt on the server") && kept.Contains("I/O error"), "message: a file that could not be put back says where its old content is");
    }

    // ---------------------------------------------------------------- the real behaviour, in every shell

    private static void RealFileTests(Shell shell)
    {
        string label = "[" + shell.Name + "] ";
        string oldText = string.Concat(Enumerable.Repeat("old line of the configuration\n", 100));
        string newText = string.Concat(Enumerable.Repeat("new line of the configuration\n", 120));

        using (Work w = new Work())
        {
            string target = w.Write("target.conf", oldText);
            string upload = w.Upload(newText);
            Result r = Run(shell, w.Script(target, upload), new Options());
            Check(r.Exit == 0 && File.ReadAllText(target) == newText, label + "the new text replaces the old one");
            Check(!File.Exists(RemoteSaveCommand.RestoreCopyPath(upload)) && File.Exists(upload), label + "the restore copy is gone, the uploaded text is left to the caller");
            Check(r.Output.Count == 0, label + "a save that works prints nothing");
        }

        using (Work w = new Work())
        {
            string target = Path.Combine(w.Root, "new.conf");
            Result r = Run(shell, w.Script(target, w.Upload(newText)), new Options());
            Check(r.Exit == 0 && File.ReadAllText(target) == newText, label + "a new file is created");
        }

        using (Work w = new Work())
        {
            string target = w.Write("empty.conf", oldText);
            Result r = Run(shell, w.Script(target, w.Upload("")), new Options());
            Check(r.Exit == 0 && new FileInfo(target).Length == 0, label + "an empty text empties the file");
        }

        // The write cannot finish (the size limit of the process stands for a file system that is full): the old file comes back.
        using (Work w = new Work())
        {
            string target = w.Write("target.conf", oldText);
            string upload = w.Upload(new string('x', 400000));
            Result r = Run(shell, w.Script(target, upload), new Options { LimitBlocks = 64 });
            Check(r.Exit == RemoteSaveCommand.ExitRestored && File.ReadAllText(target) == oldText, label + "a write that fails half way puts the old file back");
            Check(r.Text.Contains("smartty-restored") && r.Output.Any(line => !line.StartsWith("smartty-")), label + "and says so, with the message of cat");
            Check(!File.Exists(RemoteSaveCommand.RestoreCopyPath(upload)), label + "the restore copy is removed after it was used");
            Check(RemoteSaveCommand.Describe(r.Exit, r.Output, target, upload).Contains("put back"), label + "the message for the user");
        }

        using (Work w = new Work())
        {
            string target = Path.Combine(w.Root, "new.conf");
            string upload = w.Upload(new string('x', 400000));
            Result r = Run(shell, w.Script(target, upload), new Options { LimitBlocks = 64 });
            Check(r.Exit == RemoteSaveCommand.ExitRestored && !File.Exists(target) && r.Text.Contains("smartty-removed"), label + "a new file that could not be written completely is removed");
        }

        // Nothing could be written: the file has to stay as it is and the caller may offer sudo.
        using (Work w = new Work())
        {
            string target = w.Write("readonly.conf", oldText);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            string upload = w.Upload(newText);
            Options options = new Options { AsNobody = true };
            if (!CanDropPrivileges(options))
            {
                Skip(label + "no permission: the process is root and there is no setpriv to become somebody else");
            }
            else
            {
                Result r = Run(shell, w.Script(target, upload), options);
                Check(r.Exit != 0 && r.Exit < RemoteSaveCommand.ExitNoSpace && File.ReadAllText(target) == oldText, label + "no permission to write: the file is untouched and the status is the one of cat (" + r.Exit + ")");
                Check(!File.Exists(RemoteSaveCommand.RestoreCopyPath(upload)) && RemoteSaveCommand.Describe(r.Exit, r.Output, target, upload) == null, label + "no restore copy is left, and the caller is free to offer sudo");
            }
        }

        using (Work w = new Work())
        {
            string target = Path.Combine(w.Root, "new-in-a-folder-of-root.conf");
            Options options = new Options { AsNobody = true };
            if (CanDropPrivileges(options))
            {
                Result r = Run(shell, w.Script(target, w.Upload(newText)), options);
                Check(r.Exit != 0 && r.Exit < RemoteSaveCommand.ExitNoSpace && !File.Exists(target) && RemoteSaveCommand.Describe(r.Exit, r.Output, target, "x") == null, label + "no permission to create a new file: the status of cat, and sudo may be offered");
            }
            else
            {
                Skip(label + "no permission to create a new file: the process is root and there is no setpriv");
            }
        }

        // What is not a plain file is written as before.
        using (Work w = new Work())
        {
            string real = w.Write("real.conf", oldText);
            string link = Path.Combine(w.Root, "link.conf");
            File.CreateSymbolicLink(link, real);
            Result r = Run(shell, w.Script(link, w.Upload(newText)), new Options());
            Check(r.Exit == 0 && new FileInfo(link).LinkTarget != null && File.ReadAllText(real) == newText, label + "a symbolic link stays a link and the file behind it is written");

            string dangling = Path.Combine(w.Root, "dangling.conf");
            string behind = Path.Combine(w.Root, "created-through-the-link.conf");
            File.CreateSymbolicLink(dangling, behind);
            r = Run(shell, w.Script(dangling, w.Upload(newText)), new Options());
            Check(r.Exit == 0 && File.ReadAllText(behind) == newText && new FileInfo(dangling).LinkTarget != null, label + "a link to a file that is not there creates it and stays");

            string folder = Path.Combine(w.Root, "folder");
            Directory.CreateDirectory(folder);
            r = Run(shell, w.Script(folder, w.Upload(newText)), new Options());
            Check(r.Exit != 0 && r.Exit < RemoteSaveCommand.ExitNoSpace && Directory.Exists(folder), label + "a folder is not written (and nothing is made of the message)");

            r = Run(shell, w.Script("/dev/null", w.Upload(newText)), new Options());
            Check(r.Exit == 0, label + "a device is written as it was");
        }

        // The old file is copied when asked, and only then.
        using (Work w = new Work())
        {
            string target = w.Write("target.conf", oldText);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Result r = Run(shell, w.Script(target, w.Upload(newText), backup: true), new Options());
            Check(r.Exit == 0 && File.ReadAllText(target + "~") == oldText && File.ReadAllText(target) == newText, label + "the backup holds the old text");
            Check(File.GetUnixFileMode(target + "~") == File.GetUnixFileMode(target), label + "and keeps its mode (cp -p)");
            string fresh = Path.Combine(w.Root, "fresh.conf");
            r = Run(shell, w.Script(fresh, w.Upload(newText), backup: true), new Options());
            Check(r.Exit == 0 && !File.Exists(fresh + "~"), label + "a new file has nothing to back up");
            string plain = w.Write("plain.conf", oldText);
            r = Run(shell, w.Script(plain, w.Upload(newText)), new Options());
            Check(!File.Exists(plain + "~"), label + "no backup unless asked");
        }

        // Awkward names: the paths are quoted twice, for the wrapper and in the script.
        using (Work w = new Work())
        {
            string name = "we ird 'q' $HOME `x`; y\"z.conf";
            string target = w.Write(name, oldText);
            string upload = w.Upload(newText, "up load 'it' $x.txt");
            Result r = Run(shell, w.Script(target, upload), new Options());
            Check(r.Exit == 0 && File.ReadAllText(target) == newText, label + "a name with spaces, quotes, $ and a backtick is written");
            upload = w.Upload(new string('x', 400000), "up load 'it' $x.txt");
            r = Run(shell, w.Script(target, upload), new Options { LimitBlocks = 64 });
            Check(r.Exit == RemoteSaveCommand.ExitRestored && File.ReadAllText(target) == newText, label + "and put back from the restore copy with such a name");
        }
    }

    // ---------------------------------------------------------------- stand-in df and cat

    private static string DfTool => "#!/bin/sh\nif [ \"$FAKE_DF_MODE\" = garbage ]; then echo 'df: cannot read table of mounted file systems'; exit 1; fi\n"
        + "printf 'Filesystem 1024-blocks Used Available Capacity Mounted on\\n/dev/fake %s %s %s 99%% /\\n' \"$FAKE_DF_BLOCKS\" \"$FAKE_DF_USED\" \"$FAKE_DF_AVAIL\"\n";

    private static string CatTool => "#!/bin/sh\ncase \"$1\" in\n *.orig) [ \"$FAKE_CAT_RESTORE\" = fail ] && exit 1; exec /bin/cat \"$@\";;\nesac\n"
        + "case \"$FAKE_CAT_MODE\" in\n slow) head -c 10 \"$1\"; sleep 1; tail -c +11 \"$1\"; exit 0;;\n corrupt) tr 'a-z' 'A-Z' < \"$1\"; exit 0;;\n short) head -c 10 \"$1\"; exit 0;;\n fail) head -c 10 \"$1\"; echo 'cat: write error: No space left on device' >&2; exit 1;;\nesac\nexec /bin/cat \"$@\"\n";

    private static string CpTool => "#!/bin/sh\nfor last; do :; done\ncase \"$last\" in *.orig) [ \"$FAKE_CP_MODE\" = fail ] && exit 1;; esac\nexec /bin/cp \"$@\"\n";

    private static Options Df(string bin, string blocks, string used, string avail)
    {
        Options options = new Options { PathPrefix = bin };
        options.Env["FAKE_DF_BLOCKS"] = blocks;
        options.Env["FAKE_DF_USED"] = used;
        options.Env["FAKE_DF_AVAIL"] = avail;
        return options;
    }

    private static void StandInTests(Shell shell)
    {
        string label = "[" + shell.Name + "] ";
        string small = string.Concat(Enumerable.Repeat("old line of the configuration\n", 34)); // 1020 bytes
        string big50 = new string('n', 50000);

        using (Work w = new Work())
        {
            string bin = w.Tools(("df", DfTool), ("cat", CatTool), ("cp", CpTool));

            // The free space is looked at before anything is written.
            string target = w.Write("target.conf", small);
            string upload = w.Upload(big50);
            Result r = Run(shell, w.Script(target, upload), Df(bin, "1000", "990", "10"));
            Check(r.Exit == RemoteSaveCommand.ExitNoSpace && File.ReadAllText(target) == small, label + "df says 10 KiB are free and 49000 more bytes are needed: refused, the file is as it was");
            Check(r.Text.Contains("smartty-nospace 48980 10240") && !File.Exists(RemoteSaveCommand.RestoreCopyPath(upload)), label + "with the sizes in the output and no restore copy left");
            string described = RemoteSaveCommand.Describe(r.Exit, r.Output, target, upload);
            Check(described.Contains("not enough free space") && described.Contains("10240 bytes"), label + "and the message for the user says so");

            r = Run(shell, w.Script(target, upload), Df(bin, "1000", "900", "100"));
            Check(r.Exit == 0 && File.ReadAllText(target) == big50, label + "100 KiB are free: saved");

            // The old text is given back by the truncation: only the growth needs room.
            target = w.Write("grow.conf", new string('o', 50000));
            upload = w.Upload(new string('n', 60000));
            r = Run(shell, w.Script(target, upload), Df(bin, "1000", "990", "12"));
            Check(r.Exit == 0 && File.ReadAllText(target) == new string('n', 60000), label + "50000 bytes become 60000 with 12 KiB free: the 10000 bytes of growth fit");
            target = w.Write("grow2.conf", new string('o', 50000));
            r = Run(shell, w.Script(target, upload), Df(bin, "1000", "992", "8"));
            Check(r.Exit == RemoteSaveCommand.ExitNoSpace && File.ReadAllText(target) == new string('o', 50000), label + "with 8 KiB free they do not");
            target = w.Write("shrink.conf", new string('o', 50000));
            r = Run(shell, w.Script(target, w.Upload("short")), Df(bin, "1000", "1000", "0"));
            Check(r.Exit == 0 && File.ReadAllText(target) == "short", label + "a file that gets smaller needs no room, even on a full file system");

            // A new file needs room for all of it.
            string fresh = Path.Combine(w.Root, "fresh.conf");
            r = Run(shell, w.Script(fresh, w.Upload(big50)), Df(bin, "1000", "990", "10"));
            Check(r.Exit == RemoteSaveCommand.ExitNoSpace && !File.Exists(fresh), label + "a new file that does not fit is not created");

            // Only a clear answer of df counts.
            target = w.Write("unclear.conf", small);
            Options garbage = Df(bin, "1000", "990", "10");
            garbage.Env["FAKE_DF_MODE"] = "garbage";
            r = Run(shell, w.Script(target, w.Upload(big50)), garbage);
            Check(r.Exit == 0 && File.ReadAllText(target) == big50, label + "a df that fails does not stop the save");
            target = w.Write("pseudo.conf", small);
            r = Run(shell, w.Script(target, w.Upload(big50)), Df(bin, "0", "0", "0"));
            Check(r.Exit == 0 && File.ReadAllText(target) == big50, label + "a file system without a size (fuse, 9p) does not stop it either");
            r = Run(shell, w.Script(target, w.Upload(big50)), Df(bin, "-", "-", "-"));
            Check(r.Exit == 0, label + "nor do dashes");

            // A write that went through (cat said 0) but that did not read back as written.
            target = w.Write("short.conf", small);
            upload = w.Upload(big50);
            Options shortWrite = new Options { PathPrefix = bin };
            shortWrite.Env["FAKE_CAT_MODE"] = "short";
            shortWrite.Env["FAKE_DF_MODE"] = "garbage";
            r = Run(shell, w.Script(target, upload), shortWrite);
            Check(r.Exit == RemoteSaveCommand.ExitRestored && File.ReadAllText(target) == small, label + "a cat that says 0 and wrote 10 bytes is found by the comparison, the old file is back");
            Check(r.Text.Contains("smartty-restored") && !File.Exists(RemoteSaveCommand.RestoreCopyPath(upload)), label + "and the restore copy is removed");

            // The same number of bytes with other content: the size says nothing, the comparison does.
            target = w.Write("corrupt.conf", small);
            upload = w.Upload(new string('a', 5000));
            Options corrupt = new Options { PathPrefix = bin };
            corrupt.Env["FAKE_CAT_MODE"] = "corrupt";
            corrupt.Env["FAKE_DF_MODE"] = "garbage";
            r = Run(shell, w.Script(target, upload), corrupt);
            Check(r.Exit == RemoteSaveCommand.ExitRestored && File.ReadAllText(target) == small, label + "a write of the right size and the wrong bytes is found by cmp, the old file is back");

            // No restore copy can be made (no room, or the folder of the temporary files is read-only): nothing can be put back,
            // so the file is written as it always was, and the status is the one of cat.
            target = w.Write("nocopy.conf", small);
            upload = w.Upload(big50);
            Options noCopy = new Options { PathPrefix = bin };
            noCopy.Env["FAKE_CP_MODE"] = "fail";
            noCopy.Env["FAKE_DF_MODE"] = "garbage";
            r = Run(shell, w.Script(target, upload), noCopy);
            Check(r.Exit == 0 && File.ReadAllText(target) == big50 && !File.Exists(RemoteSaveCommand.RestoreCopyPath(upload)), label + "without a restore copy the file is written all the same");
            target = w.Write("nocopy2.conf", small);
            noCopy.Env["FAKE_CAT_MODE"] = "fail";
            r = Run(shell, w.Script(target, upload), noCopy);
            Check(r.Exit == 1 && !r.Text.Contains("smartty-") && RemoteSaveCommand.Describe(r.Exit, r.Output, target, upload) == null, label + "and a failed write then ends as it did before: the status of cat, no claim that anything was put back");

            // The connection is lost half way through the write (the sudo command has a terminal, which is hung up): the command goes on.
            target = w.Write("hangup.conf", small);
            upload = w.Upload(big50);
            Options hangup = new Options { PathPrefix = bin, HangupAfterMilliseconds = 400 };
            hangup.Env["FAKE_CAT_MODE"] = "slow";
            hangup.Env["FAKE_DF_MODE"] = "garbage";
            r = Run(shell, w.Script(target, upload), hangup);
            Check(r.Exit == 0 && File.ReadAllText(target) == big50 && !File.Exists(RemoteSaveCommand.RestoreCopyPath(upload)), label + "a hangup in the middle of the write: it is finished, read back and cleaned up all the same");

            // Nothing can put it back: the old content must survive in the restore copy.
            target = w.Write("lost.conf", small);
            upload = w.Upload(big50);
            Options lost = new Options { PathPrefix = bin };
            lost.Env["FAKE_CAT_MODE"] = "fail";
            lost.Env["FAKE_CAT_RESTORE"] = "fail";
            lost.Env["FAKE_DF_MODE"] = "garbage";
            r = Run(shell, w.Script(target, upload), lost);
            string copy = RemoteSaveCommand.RestoreCopyPath(upload);
            Check(r.Exit == RemoteSaveCommand.ExitNotRestored && File.Exists(copy) && File.ReadAllText(copy) == small, label + "when the old content cannot be put back it stays in the restore copy");
            Check(File.Exists(upload) && File.ReadAllText(upload) == big50 && r.Text.Contains("smartty-kept " + copy), label + "and the new text is untouched, the output names the copy");
            // The file may hold passwords: the copy is for its owner only, like the uploaded text (0600).
            Check((File.GetUnixFileMode(copy) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0, label + "the restore copy of a file that is readable by everybody is readable by its owner only");
            Check(RemoteSaveCommand.Describe(r.Exit, r.Output, target, upload).Contains(copy), label + "so does the message for the user");
        }
    }

    // ---------------------------------------------------------------- file systems that really are full (root can mount)

    private static void SmallFileSystemTests(List<Shell> shells)
    {
        using (Work w = new Work())
        {
            string mount = Path.Combine(w.Root, "small");
            Directory.CreateDirectory(mount);
            if (!Mount(mount))
            {
                Skip("a tmpfs of 64 KiB cannot be mounted (this needs root): the checks on a really full file system are not run");
                return;
            }
            try
            {
                string oldText = string.Concat(Enumerable.Repeat("old line of the configuration\n", 900)); // ~27 KB
                string newText = new string('n', 90000);
                foreach (Shell shell in shells)
                {
                    string label = "[" + shell.Name + "] ";
                    string target = Path.Combine(mount, "network.conf");
                    File.WriteAllText(target, oldText);
                    string upload = w.Upload(newText);

                    // What the editor sent before: cat into the file. This is the bug.
                    Result plain = Run(shell, "cat " + RemotePathUtils.ShellQuote(upload) + " > " + RemotePathUtils.ShellQuote(target), new Options());
                    Check(plain.Exit != 0 && File.ReadAllText(target) != oldText, label + "(the old command: the full file system leaves a file that is cut off)");

                    File.WriteAllText(target, oldText);
                    Result r = Run(shell, w.Script(target, upload), new Options());
                    Check(r.Exit == RemoteSaveCommand.ExitNoSpace && File.ReadAllText(target) == oldText, label + "df of the real file system: refused, the file is as it was");
                    Check(RemoteSaveCommand.Describe(r.Exit, r.Output, target, upload).Contains("more are needed"), label + "with the numbers of df in the message");

                    // The free space check is fooled (the length says 1000): the write itself runs out of space.
                    string script = RemoteSaveCommand.BuildScript(target, upload, 1000, backup: false);
                    r = Run(shell, script, new Options());
                    Check(r.Exit == RemoteSaveCommand.ExitRestored && File.ReadAllText(target) == oldText, label + "the write runs out of space half way: the old file is back");
                    Check(r.Output.Any(line => line.Contains("No space left on device")) && !File.Exists(RemoteSaveCommand.RestoreCopyPath(upload)), label + "the error is in the output, the restore copy is removed");

                    string fresh = Path.Combine(mount, "fresh.conf");
                    r = Run(shell, RemoteSaveCommand.BuildScript(fresh, upload, 1000, backup: false), new Options());
                    Check(r.Exit == RemoteSaveCommand.ExitRestored && !File.Exists(fresh), label + "a new file that ran out of space is removed");

                    string small = w.Upload("fits");
                    r = Run(shell, w.Script(target, small), new Options());
                    Check(r.Exit == 0 && File.ReadAllText(target) == "fits", label + "a text that fits is saved on the same file system");
                    File.Delete(fresh);
                }
            }
            finally
            {
                Unmount(mount);
            }
        }
    }

    // ---------------------------------------------------------------- no cmp, and the login shell wrapper

    private static void WithoutCmpTests()
    {
        using (Work w = new Work())
        {
            // A PATH with the tools the script uses and no cmp: the sizes are compared instead.
            string only = Path.Combine(w.Root, "only");
            Directory.CreateDirectory(only);
            foreach (string tool in new[] { "cat", "cp", "rm", "wc", "df", "head" })
            {
                string source = new[] { "/bin/" + tool, "/usr/bin/" + tool }.FirstOrDefault(File.Exists);
                File.CreateSymbolicLink(Path.Combine(only, tool), source);
            }
            Shell sh = new Shell { Name = "/bin/sh", Argv = new[] { BinSh, "-c" } };
            Options options = new Options { ExactPath = only };
            string oldText = string.Concat(Enumerable.Repeat("old line\n", 200));
            string target = w.Write("target.conf", oldText);
            string upload = w.Upload("new text\n");
            Result r = Run(sh, w.Script(target, upload), options);
            Check(r.Exit == 0 && File.ReadAllText(target) == "new text\n", "[no cmp] a save is verified by the size");
            target = w.Write("target2.conf", oldText);
            upload = w.Upload(new string('x', 400000));
            options.LimitBlocks = 64;
            r = Run(sh, w.Script(target, upload), options);
            Check(r.Exit == RemoteSaveCommand.ExitRestored && File.ReadAllText(target) == oldText, "[no cmp] a write that fails half way is found by the size and put back");
        }
    }

    private static void LoginShellTests()
    {
        using (Work w = new Work())
        {
            string oldText = string.Concat(Enumerable.Repeat("old line\n", 200));
            string target = w.Write("we ird 'q' $HOME.conf", oldText);
            string upload = w.Upload("new text\n", "up 'it' $x.txt");
            string command = PosixShell.Wrap(w.Script(target, upload));
            // What the SSH exec channel hands to the login shell.
            Result r = Run(new Shell { Name = "login shell", Argv = new[] { BinSh, "-c" } }, command, new Options());
            Check(r.Exit == 0 && File.ReadAllText(target) == "new text\n", "[login shell] the wrapped script runs and saves");
            upload = w.Upload(new string('x', 400000), "up 'it' $x.txt");
            command = PosixShell.Wrap(w.Script(target, upload));
            r = Run(new Shell { Name = "login shell", Argv = new[] { BinSh, "-c" } }, command, new Options { LimitBlocks = 64 });
            Check(r.Exit == RemoteSaveCommand.ExitRestored && File.ReadAllText(target) == "new text\n", "[login shell] and puts the file back through the wrapper too");
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed class Work : IDisposable
    {
        public readonly string Root;

        public readonly string Up;

        public Work()
        {
            Root = Path.Combine(Path.GetTempPath(), "smartty-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Up = Path.Combine(Root, "up");
            Directory.CreateDirectory(Up);
            // The command makes its restore copy next to the uploaded text: somebody else than the owner may have to.
            File.SetUnixFileMode(Up, (UnixFileMode)Convert.ToInt32("777", 8));
        }

        public string Write(string name, string content)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, content);
            return path;
        }

        public string Upload(string content, string name = null)
        {
            string path = Path.Combine(Up, name ?? (Guid.NewGuid().ToString() + ".txt"));
            File.WriteAllText(path, content);
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("644", 8));
            return path;
        }

        public string Script(string target, string uploaded, bool backup = false)
        {
            return RemoteSaveCommand.BuildScript(target, uploaded, new FileInfo(uploaded).Length, backup);
        }

        public string Tools(params (string name, string content)[] tools)
        {
            string bin = Path.Combine(Root, "bin");
            Directory.CreateDirectory(bin);
            foreach ((string name, string content) in tools)
            {
                string path = Path.Combine(bin, name);
                File.WriteAllText(path, content);
                File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
            }
            return bin;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch
            {
            }
        }
    }

    private static bool? _isRoot;

    private static bool IsRoot()
    {
        if (_isRoot == null)
        {
            Process p = Process.Start(new ProcessStartInfo("id", "-u") { RedirectStandardOutput = true });
            _isRoot = p.StandardOutput.ReadToEnd().Trim() == "0";
            p.WaitForExit();
        }
        return _isRoot.Value;
    }

    private static bool CanDropPrivileges(Options options)
    {
        if (!IsRoot())
        {
            options.AsNobody = false;
            return true;
        }
        return File.Exists("/usr/bin/setpriv") || File.Exists("/bin/setpriv");
    }

    private static Result Run(Shell shell, string script, Options options)
    {
        List<string> argv = new List<string>();
        if (options.AsNobody && IsRoot())
        {
            argv.AddRange(new[] { "setpriv", "--reuid=65534", "--regid=65534", "--clear-groups" });
        }
        if (options.LimitBlocks != null)
        {
            // A write beyond the limit fails with an error instead of ending the process with a signal.
            argv.AddRange(new[] { BinSh, "-c", "trap '' XFSZ; ulimit -f " + options.LimitBlocks + "; exec \"$0\" \"$@\"" });
        }
        argv.AddRange(shell.Argv);
        argv.Add(script);
        ProcessStartInfo start = new ProcessStartInfo(argv[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in argv.Skip(1))
        {
            start.ArgumentList.Add(arg);
        }
        string path = options.ExactPath ?? (options.PathPrefix == null ? "" : options.PathPrefix + ":") + Environment.GetEnvironmentVariable("PATH");
        start.Environment["PATH"] = path;
        foreach (KeyValuePair<string, string> pair in options.Env)
        {
            start.Environment[pair.Key] = pair.Value;
        }
        Result result = new Result();
        object gate = new object();
        using (Process process = Process.Start(start))
        {
            DataReceivedEventHandler collect = (object sender, DataReceivedEventArgs e) =>
            {
                if (e.Data != null)
                {
                    lock (gate)
                    {
                        result.Output.Add(e.Data);
                    }
                }
            };
            process.OutputDataReceived += collect;
            process.ErrorDataReceived += collect;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (options.HangupAfterMilliseconds != null)
            {
                Thread.Sleep(options.HangupAfterMilliseconds.Value);
                // What a hung-up terminal does: the signal goes to the whole group of the command, children included.
                using (Process.Start(new ProcessStartInfo("pkill", "-HUP -P " + process.Id) { UseShellExecute = false }))
                {
                }
                Thread.Sleep(50);
                using (Process.Start(new ProcessStartInfo("kill", "-HUP " + process.Id) { UseShellExecute = false }))
                {
                }
            }
            if (!process.WaitForExit(60000))
            {
                process.Kill();
                throw new Exception("The command did not finish: " + script);
            }
            process.WaitForExit();
            result.Exit = process.ExitCode;
        }
        return result;
    }

    private static bool Mount(string path)
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo("mount", "-t tmpfs -o size=64k tmpfs \"" + path + "\"") { RedirectStandardError = true, RedirectStandardOutput = true });
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Unmount(string path)
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo("umount", "\"" + path + "\"") { RedirectStandardError = true, RedirectStandardOutput = true });
            p.WaitForExit();
        }
        catch
        {
        }
    }
}
