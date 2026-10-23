using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using SmarTTY.LocalRepo;
using SmarTTY.Ssh;

// Regression harness for the local OpenWrt repository server (Tools > Local OpenWrt repository server): the opkg index that is
// generated from .ipk files is compared with what OpenWrt's ipkg-make-index.sh pipeline (tar + sed + sha256sum) produces, the
// HTTP server is exercised with HttpClient, raw sockets (path traversal) and wget, and the shell scripts that add and remove
// the feeds on the device are run by the system "sh" and by BusyBox ash (the shell of OpenWrt).
// Run with: dotnet run --project tests/LocalRepoTests
internal static class Program
{
    private static int _passed;
    private static int _skipped;
    private static string _work;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine("SKIP: " + name + " (" + reason + ")");
    }

    private static int Run(string file, string arguments, string workDir, out string output, out string error, string stdin = null)
    {
        ProcessStartInfo info = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["LC_ALL"] = "C.UTF-8";
        if (workDir != null)
        {
            info.WorkingDirectory = workDir;
        }
        using (Process process = Process.Start(info))
        {
            if (stdin != null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            error = errorTask.Result;
            return process.ExitCode;
        }
    }

    private static int RunArgs(string file, string[] arguments, out string output, out string error)
    {
        ProcessStartInfo info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment["LC_ALL"] = "C.UTF-8";
        using (Process process = Process.Start(info))
        {
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            error = errorTask.Result;
            return process.ExitCode;
        }
    }

    private static bool Has(string program)
    {
        try
        {
            return Run(program, "--version", null, out _, out _) == 0 || Run(program, "--help", null, out _, out _) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates an .ipk the way OpenWrt's ipkg-build does: a gzip-compressed TAR with debian-binary, data.tar.gz and control.tar.gz.</summary>
    private static string MakeIpk(string directory, string name, string version, string description, int dataKilobytes = 1)
    {
        string staging = Path.Combine(_work, "stage-" + Guid.NewGuid().ToString("N"));
        string ctl = Path.Combine(staging, "ctl");
        string data = Path.Combine(staging, "data", "usr", "bin");
        Directory.CreateDirectory(ctl);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(ctl, "control"),
            "Package: " + name + "\nVersion: " + version + "\nDepends: libc\nSource: package/" + name + "\nSection: utils\nMaintainer: Test <test@example.com>\nArchitecture: arm_cortex-a9_neon\nInstalled-Size: 1234\nDescription: " + description + "\n second line\n");
        File.WriteAllBytes(Path.Combine(data, name), new byte[dataKilobytes * 1024]);
        File.WriteAllText(Path.Combine(staging, "debian-binary"), "2.0\n");
        string control = Path.Combine(staging, "control.tar.gz");
        string dataTar = Path.Combine(staging, "data.tar.gz");
        Check(Run("tar", "-czf control.tar.gz -C ctl ./control", staging, out _, out string e1) == 0, "tar control.tar.gz " + e1);
        Check(Run("tar", "-czf data.tar.gz -C data .", staging, out _, out string e2) == 0, "tar data.tar.gz " + e2);
        string ipk = Path.Combine(directory, name + "_" + version + "_arm_cortex-a9_neon.ipk");
        Directory.CreateDirectory(directory);
        Check(Run("tar", "-czf " + ipk + " ./debian-binary ./data.tar.gz ./control.tar.gz", staging, out _, out string e3) == 0, "tar ipk " + e3);
        return ipk;
    }

    /// <summary>The pipeline of scripts/ipkg-make-index.sh of OpenWrt, with sha256sum in place of mkhash.</summary>
    private static string ReferenceIndex(string directory)
    {
        string script = "for pkg in `find . -name '*.ipk' | sort`; do " +
            "name=\"${pkg##*/}\"; name=\"${name%%_*}\"; " +
            "[ \"$name\" = kernel ] && continue; [ \"$name\" = libc ] && continue; " +
            "file_size=$(stat -L -c%s $pkg); sha256sum=$(sha256sum $pkg | cut -d' ' -f1); " +
            "sed_safe_pkg=`echo $pkg | sed -e 's/^\\.\\///g'`; " +
            "tar -xzOf $pkg ./control.tar.gz | tar xzOf - ./control | sed -e \"s/^Description:/Filename: $sed_safe_pkg\\\\\nSize: $file_size\\\\\nSHA256sum: $sha256sum\\\\\nDescription:/\"; " +
            "echo \"\"; done";
        ProcessStartInfo info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        using (Process process = Process.Start(info))
        {
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
    }

    private static async Task<(int status, byte[] body)> GetAsync(HttpClient client, string url, HttpMethod method = null)
    {
        using (HttpRequestMessage request = new HttpRequestMessage(method ?? HttpMethod.Get, url))
        using (HttpResponseMessage response = await client.SendAsync(request))
        {
            return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
        }
    }

    private static string RawRequest(int port, string requestTarget, out int status)
    {
        using (TcpClient tcp = new TcpClient("127.0.0.1", port))
        {
            NetworkStream stream = tcp.GetStream();
            byte[] request = Encoding.ASCII.GetBytes("GET " + requestTarget + " HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
            stream.Write(request, 0, request.Length);
            using (MemoryStream all = new MemoryStream())
            {
                stream.CopyTo(all);
                string text = Encoding.UTF8.GetString(all.ToArray());
                status = text.StartsWith("HTTP/1.1 ") ? int.Parse(text.Substring(9, 3)) : -1;
                return text;
            }
        }
    }

    private static void IndexTests()
    {
        string dir = Path.Combine(_work, "index");
        MakeIpk(dir, "hello", "1.0-1", "Hello package");
        MakeIpk(dir, "zebra-utils", "2.5-3", "Zebra utilities", dataKilobytes: 40);
        MakeIpk(dir, "kernel", "5.15", "Meta package that must be skipped");
        MakeIpk(dir, "libc", "1.2", "Another meta package that must be skipped");

        IpkIndexBuilder.Index index = IpkIndexBuilder.Build(dir);
        string text = Encoding.UTF8.GetString(index.Packages);
        Check(index.Count == 2, "index lists the 2 packages and skips kernel and libc");
        Check(text.Contains("Package: hello") && text.Contains("Package: zebra-utils") && !text.Contains("Package: kernel"), "index content");
        Check(text.Contains("Filename: hello_1.0-1_arm_cortex-a9_neon.ipk"), "Filename field is the bare file name");
        Check(text.Contains("Size: " + new FileInfo(Path.Combine(dir, "hello_1.0-1_arm_cortex-a9_neon.ipk")).Length), "Size field");
        Check(text.IndexOf("Filename:", StringComparison.Ordinal) < text.IndexOf("Description:", StringComparison.Ordinal), "file fields are placed before Description");
        Check(text.Contains("Description: Hello package\n second line\n"), "multi-line Description is kept");
        Check(text.EndsWith("\n\n") && !text.Contains("\r"), "package records are separated by an empty line, LF endings");

        if (Has("bash") && Has("tar") && Has("sed") && Has("sha256sum"))
        {
            Check(ReferenceIndex(dir) == text, "index equals the output of the ipkg-make-index.sh pipeline");
        }
        else
        {
            Skip("index equals ipkg-make-index.sh pipeline", "bash/tar/sed/sha256sum are missing");
        }

        using (MemoryStream gz = new MemoryStream(index.PackagesGz))
        using (GZipStream unzip = new GZipStream(gz, CompressionMode.Decompress))
        using (MemoryStream plain = new MemoryStream())
        {
            unzip.CopyTo(plain);
            Check(plain.ToArray().SequenceEqual(index.Packages), "Packages.gz unpacks to Packages");
        }

        string broken = Path.Combine(_work, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "bad_1.0_x.ipk"), "this is not an ipk");
        MakeIpk(broken, "good", "1.0", "Good");
        IpkIndexBuilder.Index withBad = IpkIndexBuilder.Build(broken);
        Check(withBad.Count == 1 && withBad.Warnings.Count == 1 && withBad.Warnings[0].StartsWith("bad_1.0_x.ipk"), "a damaged .ipk is skipped with a warning, the others are indexed");

        string longName = Path.Combine(_work, "longname");
        Directory.CreateDirectory(longName);
        string longPackage = new string('a', 60) + "-" + new string('b', 60);
        MakeIpk(longName, longPackage, "1.0", "Long file name");
        Check(IpkIndexBuilder.Build(longName).Count == 1, "an .ipk with a file name longer than 100 characters is indexed");
    }

    private static void StaleTests()
    {
        string dir = Path.Combine(_work, "stale");
        string ipk = MakeIpk(dir, "pkg", "1.0", "Stale test");
        Check(IpkIndexBuilder.NeedsGeneratedIndex(dir), "no index: it has to be generated");
        File.WriteAllText(Path.Combine(dir, "Packages"), "Package: from-openwrt\n\n");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "Packages"), File.GetLastWriteTimeUtc(ipk).AddMinutes(5));
        Check(!IpkIndexBuilder.NeedsGeneratedIndex(dir), "a fresh index of the build is kept");
        File.SetLastWriteTimeUtc(ipk, File.GetLastWriteTimeUtc(Path.Combine(dir, "Packages")).AddMinutes(5));
        Check(IpkIndexBuilder.NeedsGeneratedIndex(dir), "a package newer than the index makes it stale");

        // An index of the build that is older than the files only because they were copied or touched later still describes
        // them: it is served (with the signature of the build) instead of a generated, unsigned one.
        string touched = Path.Combine(_work, "touched");
        string first = MakeIpk(touched, "alpha", "1.0", "First");
        string second = MakeIpk(touched, "beta", "2.0", "Second");
        string Entry(string file) => "Package: " + Path.GetFileNameWithoutExtension(file).Split('_')[0] + "\nFilename: " + Path.GetFileName(file) + "\nSize: " + new FileInfo(file).Length + "\nSHA256sum: 00\n\n";
        string touchedIndex = Path.Combine(touched, "Packages");
        File.WriteAllText(touchedIndex, Entry(first) + Entry(second));
        File.SetLastWriteTimeUtc(touchedIndex, DateTime.UtcNow.AddHours(-2));
        Check(!IpkIndexBuilder.NeedsGeneratedIndex(touched), "files newer than the index that it still lists with the same sizes: the index of the build is kept");
        File.WriteAllText(touchedIndex, Entry(first) + Entry(second).Replace("Size: " + new FileInfo(second).Length, "Size: 1"));
        File.SetLastWriteTimeUtc(touchedIndex, DateTime.UtcNow.AddHours(-2));
        Check(IpkIndexBuilder.NeedsGeneratedIndex(touched), "a package of another size than the index says: generated");
        File.WriteAllText(touchedIndex, Entry(first));
        File.SetLastWriteTimeUtc(touchedIndex, DateTime.UtcNow.AddHours(-2));
        Check(IpkIndexBuilder.NeedsGeneratedIndex(touched), "a package that the index does not list: generated");
        File.WriteAllText(touchedIndex, Entry(first) + Entry(second) + "Package: gone\nFilename: gone_1.0_all.ipk\nSize: 5\n\n");
        File.SetLastWriteTimeUtc(touchedIndex, DateTime.UtcNow.AddHours(-2));
        Check(IpkIndexBuilder.NeedsGeneratedIndex(touched), "a package in the index that is no longer there: generated");
        File.Delete(touchedIndex);
        using (FileStream gz = File.Create(touchedIndex + ".gz"))
        using (GZipStream zip = new GZipStream(gz, CompressionMode.Compress))
        {
            byte[] text = new UTF8Encoding(false).GetBytes(Entry(first) + Entry(second));
            zip.Write(text, 0, text.Length);
        }
        File.SetLastWriteTimeUtc(touchedIndex + ".gz", DateTime.UtcNow.AddHours(-2));
        Check(!IpkIndexBuilder.NeedsGeneratedIndex(touched), "the same holds when only Packages.gz is there");

        string onlyIndex = Path.Combine(_work, "onlyindex");
        Directory.CreateDirectory(onlyIndex);
        File.WriteAllText(Path.Combine(onlyIndex, "Packages"), "x");
        Check(!IpkIndexBuilder.NeedsGeneratedIndex(onlyIndex), "an index without .ipk files is never regenerated");
    }

    private static string Sha256Hex(byte[] data)
    {
        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
        {
            return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }

    /// <summary>The scripts and parsers of the temporary signing key (see OpkgSigning).</summary>
    private static void SigningScriptTests()
    {
        Check(OpkgSigning.ParseSetup(new[] { "noise", "SMARTTY_SIGN=0123456789abcdef" }) == "0123456789abcdef", "setup: the fingerprint is read");
        Check(OpkgSigning.ParseSetup(new[] { "SMARTTY_SIGN=nousign" }) == OpkgSigning.NoUsign, "setup: a device without usign is told apart");
        Check(OpkgSigning.ParseSetup(new[] { "SMARTTY_SIGN=../../etc" }) == null && OpkgSigning.ParseSetup(new string[0]) == null && OpkgSigning.ParseSetup(null) == null, "setup: anything that is not a hex fingerprint is refused (it becomes a file name)");
        byte[] signature = OpkgSigning.ParseSignature(new[] { "untrusted comment: signed by key 0123", "RWQ8mSig==", "" });
        Check(signature != null && Encoding.UTF8.GetString(signature) == "untrusted comment: signed by key 0123\nRWQ8mSig==\n", "signature: the two lines of the signature file are taken");
        Check(OpkgSigning.ParseSignature(new[] { "wget: server returned error" }) == null && OpkgSigning.ParseSignature(new[] { "untrusted comment: x" }) == null && OpkgSigning.ParseSignature(null) == null, "signature: an error message is no signature");
        string sign = OpkgSigning.BuildSignScript("http://127.0.0.1:8765/r0/base/Packages");
        Check(sign.Contains("uclient-fetch") && sign.Contains("'http://127.0.0.1:8765/r0/base/Packages'") && sign.Contains("usign -S"), "sign script: the device fetches the index itself and signs it");
        Check(OpkgSigning.BuildSetupScript().Contains("usign -G") && OpkgSigning.BuildSetupScript().Contains("umask 077"), "setup script: the key pair is made by usign, owner-only");
    }

    /// <summary>Typed folder paths: cleaning, the spellings of a WSL path, and the description of a path that was not found.</summary>
    private static void FolderPathTests()
    {
        const string wsl = "\\\\wsl.localhost\\Ubuntu-24.04\\home\\alex\\openwrt\\bin\\targets\\zynq\\generic\\packages";
        Check(LocalRepoFolders.Clean("  " + wsl + "  ") == wsl, "clean: blanks around a WSL path are taken away");
        Check(LocalRepoFolders.Clean("\"" + wsl + "\\\"") == wsl, "clean: quotes (Copy as path) and a trailing backslash are taken away");
        Check(LocalRepoFolders.Clean("//wsl.localhost/Ubuntu-24.04/home/alex/") == "\\\\wsl.localhost\\Ubuntu-24.04\\home\\alex", "clean: a path with forward slashes that starts with two of them becomes a network path");
        Check(LocalRepoFolders.Clean("C:") == "C:\\" && LocalRepoFolders.Clean("C:\\") == "C:\\", "clean: the root of a drive keeps its backslash");
        Check(LocalRepoFolders.Clean("\\\\server\\share\\") == "\\\\server\\share" && LocalRepoFolders.Clean("\\\\server") == "\\\\server", "clean: a share is not shortened to the server");
        Check(LocalRepoFolders.Clean(null) == "" && LocalRepoFolders.Clean("   ") == "" && LocalRepoFolders.Clean("\"\"") == "", "clean: nothing typed is nothing");
        Check(LocalRepoFolders.Clean("/home/alex/openwrt/") == "/home/alex/openwrt", "clean: a Unix path loses only the trailing slash");

        Check(LocalRepoFolders.Candidates(wsl).SequenceEqual(new[] { wsl, "\\\\wsl$\\Ubuntu-24.04\\home\\alex\\openwrt\\bin\\targets\\zynq\\generic\\packages" }), "candidates: \\wsl.localhost\\ has the older \\wsl$\\ spelling");
        Check(LocalRepoFolders.Candidates("\\\\WSL$\\Debian\\x").Last() == "\\\\wsl.localhost\\Debian\\x", "candidates: and the other way round, whatever the case");
        Check(LocalRepoFolders.Candidates("D:\\build").Count() == 1, "candidates: another path has one spelling");
        Check(LocalRepoFolders.FindExisting(wsl, p => p.StartsWith("\\\\wsl$\\")) == "\\\\wsl$\\Ubuntu-24.04\\home\\alex\\openwrt\\bin\\targets\\zynq\\generic\\packages", "find: the spelling that exists is the one that is used");
        Check(LocalRepoFolders.FindExisting(wsl, p => false) == null, "find: nothing exists");

        HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "\\\\wsl.localhost\\Ubuntu-24.04", "\\\\wsl.localhost\\Ubuntu-24.04\\home", "\\\\wsl.localhost\\Ubuntu-24.04\\home\\alex", "\\\\wsl.localhost\\Ubuntu-24.04\\home\\alex\\openwrt" };
        string Describe(string path) => LocalRepoFolders.DescribeMissing(path, existing.Contains, folder => folder.EndsWith("openwrt") ? new List<string> { "bin", "feeds", "package" } : new List<string>());
        string misspelled = Describe(wsl);
        Check(misspelled.Contains("exists up to \\\\wsl.localhost\\Ubuntu-24.04\\home\\alex\\openwrt") && misspelled.Contains("no \"bin\"") && misspelled.Contains("It holds: bin, feeds, package."), "describe: the part that exists, the name that is missing and what the folder holds");
        string nothing = LocalRepoFolders.DescribeMissing(wsl, p => false, f => new List<string>());
        Check(nothing.Contains("Nothing of this WSL path can be reached") && nothing.Contains("wsl -d") && nothing.Contains("wsl -l -v"), "describe: an unreachable WSL path says to start the distribution");
        string share = LocalRepoFolders.DescribeMissing("\\\\nas\\share\\packages", p => false, f => new List<string>());
        Check(share.Contains("\\\\nas\\share cannot be reached"), "describe: an unreachable share is named");
        string local = LocalRepoFolders.DescribeMissing("D:\\build\\bin\\packages", p => p == "D:\\build" || p == "D:", f => new List<string> { "bin2" });
        Check(local.Contains("exists up to D:\\build") && local.Contains("no \"bin\"") && local.Contains("bin2"), "describe: a local path");
        string many = LocalRepoFolders.DescribeMissing("\\\\h\\s\\a\\b", p => p == "\\\\h\\s\\a", f => Enumerable.Range(1, 30).Select(i => "d" + i).ToList());
        Check(many.Contains("d12, ...") && !many.Contains("d13"), "describe: a long list of folders is cut");
        Check(!LocalRepoFolders.DescribeMissing("\\\\h\\s\\a", p => p == "\\\\h\\s", f => throw new System.IO.IOException("denied")).Contains("holds"), "describe: a folder that cannot be listed adds nothing");
    }

    /// <summary>A build tree: the feeds of bin/packages/&lt;arch&gt; and the kernel modules of bin/targets/&lt;target&gt;/&lt;subtarget&gt;/packages as a second folder.</summary>
    private static async Task TargetPackagesTests()
    {
        string tree = Path.Combine(_work, "tree");
        string packages = Path.Combine(tree, "bin", "packages", "arm_cortex-a9_neon");
        string kmods = Path.Combine(tree, "bin", "targets", "zynq", "generic", "packages");
        MakeIpk(Path.Combine(packages, "base"), "busybox", "1.36", "Shell");
        MakeIpk(Path.Combine(packages, "luci"), "luci-base", "1.0", "Web UI");
        string kmod = MakeIpk(kmods, "kmod-usb-storage", "5.15.1-1", "USB storage");
        MakeIpk(kmods, "kernel", "5.15.1-1-abc", "The kernel meta package");
        File.WriteAllText(Path.Combine(kmods, "Packages"), "Package: kmod-usb-storage\nFilename: " + Path.GetFileName(kmod) + "\nSize: " + new FileInfo(kmod).Length + "\n\n");
        File.SetLastWriteTimeUtc(Path.Combine(kmods, "Packages"), DateTime.UtcNow.AddMinutes(5));
        File.WriteAllText(Path.Combine(kmods, "Packages.sig"), "untrusted comment: build\nRWQ=\n");

        List<LocalRepoFeed> feeds = LocalRepoFeedScanner.Scan(new[] { packages, kmods });
        Check(feeds.Select(f => f.UrlPath).SequenceEqual(new[] { "/r0/base", "/r0/luci", "/r1" }), "two folders: the feeds of the first and the second folder itself as a feed (" + string.Join(", ", feeds.Select(f => f.UrlPath)) + ")");
        Check(feeds.Select(f => f.Name).Distinct().Count() == 3 && feeds.Last().Name == "smartty_packages", "their names differ (" + string.Join(", ", feeds.Select(f => f.Name)) + ")");

        string dir = Path.Combine(_work, "tree-device");
        Directory.CreateDirectory(dir);
        string conf = Path.Combine(dir, "customfeeds.conf");
        LocalRepoHost host = new LocalRepoHost();
        SshSession session = NewFakeSession(conf);
        host.Start(session, new[] { packages, kmods }, 0);
        string written = File.ReadAllText(conf);
        Check(written.Contains("src/gz smartty_arm_cortex_a9_neon_base http://127.0.0.1:" + host.Port + "/r0/base") && written.Contains("src/gz smartty_packages http://127.0.0.1:" + host.Port + "/r1"), "both folders are written to customfeeds.conf");
        using (HttpClient client = new HttpClient())
        {
            string baseUrl = "http://127.0.0.1:" + host.Port;
            Check((await GetAsync(client, baseUrl + "/r1/Packages")).status == 200 && (await GetAsync(client, baseUrl + "/r1/" + Path.GetFileName(kmod))).body.SequenceEqual(File.ReadAllBytes(kmod)), "the kernel module feed serves its index and its packages");
            Check((await GetAsync(client, baseUrl + "/r1/Packages.sig")).status == 200, "and the signature of the build");
        }
        host.Stop(session);
    }

    /// <summary>Finding bin/targets/&lt;target&gt;/&lt;subtarget&gt;/packages for a folder of bin/packages.</summary>
    private static void TargetPackageFolderTests()
    {
        const string tree = "D:\\openwrt";
        HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            tree + "\\bin\\targets",
            tree + "\\bin\\targets\\zynq", tree + "\\bin\\targets\\zynq\\generic", tree + "\\bin\\targets\\zynq\\generic\\packages",
            tree + "\\bin\\targets\\mvebu", tree + "\\bin\\targets\\mvebu\\cortexa9", tree + "\\bin\\targets\\mvebu\\cortexa9\\packages"
        };
        IList<string> Children(string directory)
        {
            string prefix = directory.TrimEnd('\\') + "\\";
            return folders.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.IndexOf('\\', prefix.Length) < 0).Select(f => f.Substring(prefix.Length)).ToList();
        }
        Dictionary<string, string> architectures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [tree + "\\bin\\targets\\zynq\\generic\\packages"] = "arm_cortex-a9_neon",
            [tree + "\\bin\\targets\\mvebu\\cortexa9\\packages"] = "arm_cortex-a9_vfpv3-d16"
        };
        IList<string> Find(string folder) => LocalRepoFolders.FindTargetPackageFolders(folder, folders.Contains, Children, d => architectures.TryGetValue(d, out string a) ? a : null);

        IList<string> both = Find(tree + "\\bin\\packages");
        Check(both.Count == 2, "bin/packages itself: both targets that were built");
        IList<string> matching = Find(tree + "\\bin\\packages\\arm_cortex-a9_neon");
        Check(matching.Count == 1 && matching[0] == tree + "\\bin\\targets\\zynq\\generic\\packages", "bin/packages/<arch>: only the target for that architecture (" + string.Join(", ", matching) + ")");
        Check(Find(tree + "\\bin\\packages\\arm_cortex-a9_neon\\base\\").SequenceEqual(matching), "a feed inside bin/packages/<arch> leads to the same folder, a trailing backslash does not matter");
        Check(Find(tree.ToLowerInvariant() + "\\BIN\\Packages\\arm_cortex-a9_neon").Count == 1, "the case of the path does not matter");
        Check(Find("\"" + tree + "\\bin\\packages\\unknown_arch\"").Count == 2, "an architecture that no target matches: all targets are offered");
        Check(Find(tree + "\\bin\\targets\\zynq\\generic\\packages").Count == 0, "the target folder itself is not a folder of bin/packages");
        Check(Find(tree + "\\bin\\packages2\\x").Count == 0 && Find("D:\\other\\base").Count == 0 && Find("").Count == 0, "other folders have nothing to add");
        Check(LocalRepoFolders.FindTargetPackageFolders("E:\\nobin\\bin\\packages\\arm", p => false, d => new List<string>(), d => null).Count == 0, "a tree without bin/targets has nothing to add");
        Check(LocalRepoFolders.FindTargetPackageFolders(tree + "\\bin\\packages\\arm", folders.Contains, d => throw new System.IO.IOException("denied"), d => null).Count == 0, "a folder that cannot be listed adds nothing");
        Check(LocalRepoFolders.FindTargetPackageFolders("/home/a/openwrt/bin/packages/arm", p => p == "/home/a/openwrt/bin/targets" || p.EndsWith("/zynq/generic/packages"), d => d.EndsWith("targets") ? new List<string> { "zynq" } : d.EndsWith("zynq") ? new List<string> { "generic" } : new List<string>(), d => null).SequenceEqual(new[] { "/home/a/openwrt/bin/targets/zynq/generic/packages" }), "a Unix path keeps its slashes");
        string zynq = Path.Combine(_work, "arch-zynq");
        Directory.CreateDirectory(zynq);
        File.WriteAllText(Path.Combine(zynq, "Packages"), "Package: kmod-x\nVersion: 1\nArchitecture: arm_cortex-a9_neon\n\n");
        Check(LocalRepoFolders.ReadArchitecture(zynq) == "arm_cortex-a9_neon" && LocalRepoFolders.ReadArchitecture(Path.Combine(_work, "no-such")) == null, "the architecture is read from the first package of the index");
    }

    private static void ScannerTests()
    {
        string root = Path.Combine(_work, "bin", "packages", "arm_cortex-a9_neon");
        Directory.CreateDirectory(Path.Combine(root, "base"));
        File.WriteAllText(Path.Combine(root, "base", "Packages"), "");
        MakeIpk(Path.Combine(root, "packages"), "foo", "1.0", "Foo");
        Directory.CreateDirectory(Path.Combine(root, "luci"));
        Directory.CreateDirectory(Path.Combine(root, ".git", "objects"));
        File.WriteAllText(Path.Combine(root, ".git", "objects", "Packages"), "");
        Directory.CreateDirectory(Path.Combine(root, "my feed ä"));
        File.WriteAllText(Path.Combine(root, "my feed ä", "Packages.gz"), "");
        string second = Path.Combine(_work, "other", "base");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "Packages"), "");

        List<LocalRepoFeed> feeds = LocalRepoFeedScanner.Scan(new[] { root, second, Path.Combine(_work, "missing") });
        Check(feeds.Count == 4, "scanner finds 4 feeds (empty folders and hidden folders are ignored)");
        Check(feeds.Select(f => f.UrlPath).SequenceEqual(new[] { "/r0/base", "/r0/my%20feed%20%C3%A4", "/r0/packages", "/r1" }), "URL paths are escaped, root folders are r0, r1, ...");
        Check(feeds[0].Name == "smartty_arm_cortex_a9_neon_base" && feeds[3].Name == "smartty_base", "feed names are made of the folder names");
        Check(feeds.All(f => f.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')), "feed names contain only letters, digits and underscores");
        Check(feeds.Select(f => f.Name).Distinct().Count() == feeds.Count, "feed names are unique");
        List<LocalRepoFeed> direct = LocalRepoFeedScanner.Scan(new[] { Path.Combine(root, "base") });
        Check(direct.Count == 1 && direct[0].UrlPath == "/r0", "a folder that is itself a feed is served from its root");
    }

    private static void ResolveTests()
    {
        string root = Path.Combine(_work, "resolve", "repo");
        Directory.CreateDirectory(root);
        Check(LocalRepoServer.ResolveInside(root, "base/Packages") == Path.Combine(Path.GetFullPath(root), "base", "Packages"), "ResolveInside maps inside the root");
        Check(LocalRepoServer.ResolveInside(root, "../repo2/a") == null, "ResolveInside rejects '..'");
        Check(LocalRepoServer.ResolveInside(root, "a/../../x") == null, "ResolveInside rejects '..' in the middle");
        Check(LocalRepoServer.ResolveInside(root, "a\\b") == null, "ResolveInside rejects backslashes on every platform");
        Check(LocalRepoServer.ResolveInside(root, "C:") == null && LocalRepoServer.ResolveInside(root, "base/C:x") == null, "ResolveInside rejects drive letters and colons on every platform");
        Check(LocalRepoServer.ResolveInside(root, "") == Path.GetFullPath(root), "ResolveInside of an empty path is the root");
    }

    private static async Task ServerTests()
    {
        string root = Path.Combine(_work, "server", "repo");
        string feed = Path.Combine(root, "base");
        string ipk = MakeIpk(feed, "served", "1.0", "Served package", dataKilobytes: 600);
        string outside = Path.Combine(_work, "server", "secret.txt");
        File.WriteAllText(outside, "TOP-SECRET");
        string sibling = Path.Combine(_work, "server", "repo-other");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "secret.txt"), "TOP-SECRET");

        List<string> log = new List<string>();
        using (LocalRepoServer server = new LocalRepoServer(new[] { root }, 0))
        using (HttpClient client = new HttpClient())
        {
            server.Feeds = LocalRepoFeedScanner.Scan(new[] { root });
            server.RequestLogged += (string line) => { lock (log) { log.Add(line); } };
            server.Start();
            string baseUrl = "http://127.0.0.1:" + server.Port;
            Check(server.Port > 0, "the server reports the port in use");

            (int status, byte[] body) gz = await GetAsync(client, baseUrl + "/r0/base/Packages.gz");
            Check(gz.status == 200, "Packages.gz is generated and served when there is no index");
            IpkIndexBuilder.Index expected = IpkIndexBuilder.Build(feed);
            Check(gz.body.SequenceEqual(expected.PackagesGz), "served Packages.gz equals the generated one");
            (int status, byte[] body) plain = await GetAsync(client, baseUrl + "/r0/base/Packages");
            Check(plain.status == 200 && plain.body.SequenceEqual(expected.Packages), "Packages is served too");
            Check((await GetAsync(client, baseUrl + "/r0/base/Packages.sig")).status == 404, "no signature is served for a generated index");

            (int status, byte[] body) file = await GetAsync(client, baseUrl + "/r0/base/" + Path.GetFileName(ipk));
            Check(file.status == 200 && file.body.SequenceEqual(File.ReadAllBytes(ipk)), "an .ipk is served byte for byte (600 KiB)");
            (int status, byte[] body) head = await GetAsync(client, baseUrl + "/r0/base/" + Path.GetFileName(ipk), HttpMethod.Head);
            Check(head.status == 200 && head.body.Length == 0, "HEAD has no body");
            using (HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, baseUrl + "/r0/base/" + Path.GetFileName(ipk))))
            {
                Check(response.Content.Headers.ContentLength == new FileInfo(ipk).Length, "HEAD reports the Content-Length of the file");
            }
            Check((await GetAsync(client, baseUrl + "/r0/base/missing.ipk")).status == 404, "a missing file is 404");
            Check((await GetAsync(client, baseUrl + "/r1/base/Packages")).status == 404, "an unknown root is 404");
            Check((await GetAsync(client, baseUrl + "/other")).status == 404, "an unknown path is 404");
            Check((await GetAsync(client, baseUrl + "/r0/base")).status == 404, "a directory is not listed");
            (int status, byte[] body) start = await GetAsync(client, baseUrl + "/");
            Check(start.status == 200 && Encoding.UTF8.GetString(start.body).Contains("/r0/base"), "the start page lists the feeds");
            Check((await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, baseUrl + "/r0/base/Packages"))).StatusCode == System.Net.HttpStatusCode.MethodNotAllowed, "POST is 405");

            string[] attacks =
            {
                "/r0/../secret.txt", "/r0/%2e%2e/secret.txt", "/r0/..%2fsecret.txt", "/r0/%2e%2e%2fsecret.txt", "/r0/base/../../secret.txt",
                "/r0/..%5csecret.txt", "/r0/%5c..%5csecret.txt", "/r0/../repo-other/secret.txt", "/r0//..//secret.txt", "/r0/base/%00/../../../secret.txt",
                "/r0/base/..;/..;/secret.txt", "/r0/base/%252e%252e/secret.txt", "/r0/base/....//....//secret.txt"
            };
            foreach (string attack in attacks)
            {
                string response = RawRequest(server.Port, attack, out int status);
                Check(status != 200 && !response.Contains("TOP-SECRET"), "path traversal is refused: " + attack + " -> " + status);
            }

            if (Has("wget"))
            {
                // wget is what opkg uses on OpenWrt (uclient-fetch speaks the same HTTP/1.1 with Connection: close).
                string target = Path.Combine(_work, "wget-Packages.gz");
                Check(Run("wget", "-q -O " + target + " " + baseUrl + "/r0/base/Packages.gz", null, out _, out string wgetError) == 0 && File.ReadAllBytes(target).SequenceEqual(expected.PackagesGz), "wget downloads Packages.gz " + wgetError);
                string ipkTarget = Path.Combine(_work, "wget.ipk");
                Check(Run("wget", "-q -O " + ipkTarget + " " + baseUrl + "/r0/base/" + Path.GetFileName(ipk), null, out _, out _) == 0 && File.ReadAllBytes(ipkTarget).SequenceEqual(File.ReadAllBytes(ipk)), "wget downloads an .ipk");
            }
            else
            {
                Skip("wget downloads", "wget is missing");
            }

            // A fresh index written by the build is served as is, and signatures are served along with it.
            File.WriteAllText(Path.Combine(feed, "Packages"), "Package: from-openwrt\n\n");
            File.SetLastWriteTimeUtc(Path.Combine(feed, "Packages"), File.GetLastWriteTimeUtc(ipk).AddMinutes(5));
            File.WriteAllText(Path.Combine(feed, "Packages.sig"), "signature");
            (int status, byte[] body) fresh = await GetAsync(client, baseUrl + "/r0/base/Packages");
            Check(fresh.status == 200 && Encoding.UTF8.GetString(fresh.body) == "Package: from-openwrt\n\n", "a fresh Packages of the build is served as is");
            Check((await GetAsync(client, baseUrl + "/r0/base/Packages.sig")).status == 200, "Packages.sig is served with a fresh index");
            // A rebuilt package makes the index stale: it is generated again, with the new package in it.
            string rebuilt = MakeIpk(feed, "added-later", "3.0", "Built after the index");
            File.SetLastWriteTimeUtc(rebuilt, File.GetLastWriteTimeUtc(Path.Combine(feed, "Packages")).AddMinutes(5));
            (int status, byte[] body) stale = await GetAsync(client, baseUrl + "/r0/base/Packages");
            Check(stale.status == 200 && Encoding.UTF8.GetString(stale.body).Contains("Package: added-later"), "a package built after the index appears in the generated index");

            // Several clients at once.
            Task<(int status, byte[] body)>[] parallel = Enumerable.Range(0, 24).Select(_ => GetAsync(client, baseUrl + "/r0/base/" + Path.GetFileName(ipk))).ToArray();
            (int status, byte[] body)[] results = await Task.WhenAll(parallel);
            Check(results.All(r => r.status == 200 && r.body.Length == new FileInfo(ipk).Length), "24 parallel downloads succeed");

            server.Stop();
            bool refused = false;
            try
            {
                using (new TcpClient("127.0.0.1", server.Port))
                {
                }
            }
            catch (SocketException)
            {
                refused = true;
            }
            Check(refused, "the port is closed after Stop");
        }
        lock (log)
        {
            Check(log.Any(l => l.StartsWith("GET /r0/base/Packages.gz - 200") && l.Contains("index generated from 1 .ipk files")), "requests are logged with the status and a note about the generated index");
        }

        // The port is released and a busy port is reported.
        TcpListener busy = new TcpListener(System.Net.IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            using (LocalRepoServer second = new LocalRepoServer(new[] { root }, ((System.Net.IPEndPoint)busy.LocalEndpoint).Port))
            {
                bool failed = false;
                try
                {
                    second.Start();
                }
                catch (SocketException)
                {
                    failed = true;
                }
                Check(failed, "a busy port makes Start throw a SocketException");
            }
        }
        finally
        {
            busy.Stop();
        }
    }

    private static void ConfigTests()
    {
        List<LocalRepoFeed> feeds = new List<LocalRepoFeed>
        {
            new LocalRepoFeed { Name = "smartty_base", UrlPath = "/r0/base", Directory = "/x" },
            new LocalRepoFeed { Name = "smartty_packages", UrlPath = "/r0/packages", Directory = "/y" }
        };
        Check(OpkgFeedConfig.FeedLine(feeds[0], 8765) == "src/gz smartty_base http://127.0.0.1:8765/r0/base", "feed line");
        Check(OpkgFeedConfig.WrapForPrivileges("echo 'a'", true) == "echo 'a'", "root runs the script as is");
        Check(OpkgFeedConfig.WrapForPrivileges("echo 'a'", false) == "sudo -n sh -c 'echo '\\''a'\\'''", "other users go through sudo -n sh -c with the script quoted");

        List<(string name, string shell, string[] args)> shells = new List<(string, string, string[])> { ("sh", "sh", new[] { "-c" }) };
        if (Has("busybox"))
        {
            shells.Add(("busybox ash", "busybox", new[] { "ash", "-c" }));
        }
        else
        {
            Skip("feed scripts under BusyBox ash", "busybox is missing");
        }
        foreach ((string name, string shell, string[] args) in shells)
        {
            string Exec(string script, out int exit)
            {
                exit = RunArgs(shell, args.Concat(new[] { script }).ToArray(), out string output, out string error);
                return output + error;
            }

            string conf = Path.Combine(_work, "conf-" + name.Replace(' ', '-'), "opkg", "customfeeds.conf");
            string fakeBin = Path.Combine(_work, "fakebin-" + name.Replace(' ', '-'));
            string emptyBin = Path.Combine(_work, "emptybin-" + name.Replace(' ', '-'));
            Directory.CreateDirectory(fakeBin);
            Directory.CreateDirectory(emptyBin);
            File.WriteAllText(Path.Combine(fakeBin, "opkg"), "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(Path.Combine(fakeBin, "opkg"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Exec("PATH=" + fakeBin + "; " + OpkgFeedConfig.BuildCheckScript(), out int foundExit);
            Exec("PATH=" + emptyBin + "; " + OpkgFeedConfig.BuildCheckScript(), out int missingExit);
            Check(foundExit == 0 && missingExit != 0, name + ": the check script tells whether opkg exists");

            // New file in a new directory.
            Exec(OpkgFeedConfig.BuildEnableScript(feeds, 8765, conf), out int exit1);
            string first = File.ReadAllText(conf);
            Check(exit1 == 0 && first == "# smartty-local-begin\nsrc/gz smartty_base http://127.0.0.1:8765/r0/base\nsrc/gz smartty_packages http://127.0.0.1:8765/r0/packages\n# smartty-local-end\n", name + ": enable creates the file with the marked block");

            // Existing content without a trailing newline is kept, the block is not duplicated, and a changed port is applied.
            File.WriteAllText(conf, "# my own\nsrc/gz mine http://example.com/feed");
            Exec(OpkgFeedConfig.BuildEnableScript(feeds, 8765, conf), out int exit2);
            Exec(OpkgFeedConfig.BuildEnableScript(feeds.Take(1).ToList(), 9000, conf), out int exit3);
            string second = File.ReadAllText(conf);
            Check(exit2 == 0 && exit3 == 0 && second == "# my own\nsrc/gz mine http://example.com/feed\n# smartty-local-begin\nsrc/gz smartty_base http://127.0.0.1:9000/r0/base\n# smartty-local-end\n", name + ": enable keeps the user's lines, ends them with a newline and replaces the old block");

            // Lines after the block are kept when the block is removed.
            File.AppendAllText(conf, "src/gz after http://example.com/after\n");
            Exec(OpkgFeedConfig.BuildDisableScript(conf), out int exit4);
            Check(exit4 == 0 && File.ReadAllText(conf) == "# my own\nsrc/gz mine http://example.com/feed\nsrc/gz after http://example.com/after\n", name + ": disable removes only the block");
            Exec(OpkgFeedConfig.BuildDisableScript(conf), out int exit5);
            Check(exit5 == 0, name + ": disable without a block succeeds");
            Exec(OpkgFeedConfig.BuildDisableScript(Path.Combine(_work, "no-such-file.conf")), out int exit6);
            Check(exit6 == 0, name + ": disable with no configuration file succeeds");

            // A feed name or path with a quote cannot break out of the script.
            LocalRepoFeed hostile = new LocalRepoFeed { Name = "x'; touch " + Path.Combine(_work, "pwned") + "; '", UrlPath = "/r0/a'b", Directory = "/" };
            Exec(OpkgFeedConfig.BuildEnableScript(new List<LocalRepoFeed> { hostile }, 1, conf), out _);
            Check(!File.Exists(Path.Combine(_work, "pwned")), name + ": quotes in a feed name do not run commands");
        }
    }

    /// <summary>The scripts that probe, turn off and turn on again the signature check of opkg, under sh and BusyBox ash.</summary>
    private static void OpkgSignatureTests()
    {
        List<LocalRepoFeed> feeds = new List<LocalRepoFeed> { new LocalRepoFeed { Name = "smartty_base", UrlPath = "/r0/base", Directory = "/x" } };
        const string original = "dest root /\ndest ram /tmp\nlists_dir ext /var/opkg-lists\noption overlay_root /overlay\noption check_signature\n";
        foreach ((string name, string shell, string[] args) in Shells())
        {
            string Exec(string script, out int exit)
            {
                exit = RunArgs(shell, args.Concat(new[] { script }).ToArray(), out string output, out string error);
                return output + error;
            }

            bool Probe(string opkgConf)
            {
                return OpkgFeedConfig.ParseSignatureProbe(Exec(OpkgFeedConfig.BuildSignatureProbe(opkgConf), out _).Split('\n'));
            }

            string dir = Path.Combine(_work, "sig-" + name.Replace(' ', '-'));
            Directory.CreateDirectory(dir);
            string opkgConf = Path.Combine(dir, "opkg.conf");
            string missing = Path.Combine(dir, "missing.conf");
            string conf = Path.Combine(dir, "customfeeds.conf");

            File.WriteAllText(opkgConf, original);
            Check(Probe(opkgConf), name + ": the probe sees option check_signature");
            File.WriteAllText(opkgConf, "option check_signature 0\n");
            Check(!Probe(opkgConf), name + ": check_signature 0 is no check");
            File.WriteAllText(opkgConf, "option check_signature_x\n# option check_signature\n");
            Check(!Probe(opkgConf), name + ": a commented option or another option is no check");
            Check(!Probe(missing), name + ": no opkg.conf is no check");

            File.WriteAllText(opkgConf, original);
            Exec(OpkgFeedConfig.BuildEnableScript(feeds, 8765, conf, allowUnsigned: true, opkgConf: opkgConf), out int exit1);
            string off = File.ReadAllText(opkgConf);
            Check(exit1 == 0 && off == original.Replace("option check_signature\n", OpkgFeedConfig.SignatureOffMarker + "option check_signature\n") && File.ReadAllText(conf).Contains("src/gz smartty_base"), name + ": enable with unsigned feeds allowed comments the check out and adds the feeds");
            Check(!Probe(opkgConf), name + ": and the probe sees no check then");
            Exec(OpkgFeedConfig.BuildEnableScript(feeds, 8765, conf, allowUnsigned: true, opkgConf: opkgConf), out int exit2);
            Check(exit2 == 0 && File.ReadAllText(opkgConf) == off, name + ": enabling again does not add a second marker");
            Exec(OpkgFeedConfig.BuildDisableScript(conf, opkgConf), out int exit3);
            Check(exit3 == 0 && File.ReadAllText(opkgConf) == original && !File.ReadAllText(conf).Contains("smartty"), name + ": disable removes the feeds and gives the check back unchanged");

            // A run that allowed unsigned feeds left the marker (SmarTTY was killed); the next run without it restores the check.
            File.WriteAllText(opkgConf, off);
            Exec(OpkgFeedConfig.BuildEnableScript(feeds, 8765, conf, allowUnsigned: false, opkgConf: opkgConf), out int exit4);
            Check(exit4 == 0 && File.ReadAllText(opkgConf) == original, name + ": enable without unsigned feeds turns a check left off by an earlier run on again");
            File.WriteAllText(opkgConf, "option check_signature 0\n");
            Exec(OpkgFeedConfig.BuildEnableScript(feeds, 8765, conf, allowUnsigned: true, opkgConf: opkgConf), out int exit5);
            Check(exit5 == 0 && File.ReadAllText(opkgConf) == "option check_signature 0\n", name + ": an opkg.conf without the check is not touched");
            Exec(OpkgFeedConfig.BuildEnableScript(feeds, 8765, conf, allowUnsigned: true, opkgConf: missing), out int exit6);
            Exec(OpkgFeedConfig.BuildDisableScript(conf, missing), out int exit7);
            Check(exit6 == 0 && exit7 == 0 && !File.Exists(missing), name + ": no opkg.conf: enable and disable succeed without creating it");
        }
    }

    /// <summary>A stand-in for the usign of the device: -G makes the pair, -F prints a fingerprint, -S writes a signature that holds the SHA-256 of the message.</summary>
    private static string MakeFakeUsign(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "usign");
        File.WriteAllText(path,
            "#!/bin/sh\n" +
            "mode=; while getopts 'FGSVc:m:P:p:qs:x:' o; do case $o in F|G|S|V) mode=$o;; c) comment=$OPTARG;; m) msg=$OPTARG;; p) pub=$OPTARG;; s) sec=$OPTARG;; x) sigf=$OPTARG;; esac; done\n" +
            "case $mode in\n" +
            "G) printf 'untrusted comment: %s\\nRWTESTKEY\\n' \"$comment\" > \"$pub\"; echo secret > \"$sec\";;\n" +
            "F) sha256sum \"$pub\" | cut -c1-16;;\n" +
            "S) h=$(sha256sum \"$msg\" | cut -d' ' -f1); printf 'untrusted comment: signed by key test\\nRWS%s\\n' \"$h\" > \"$sigf\";;\n" +
            "*) exit 1;;\n" +
            "esac\n");
        Run("chmod", "+x " + path, null, out _, out _);
        return directory;
    }

    /// <summary>The setup, sign and remove scripts of the temporary key under sh and BusyBox ash (the shell of OpenWrt), with stand-ins for usign and uclient-fetch.</summary>
    private static void SigningShellTests()
    {
        foreach ((string name, string shell, string[] args) in Shells())
        {
            string dir = Path.Combine(_work, "signshell-" + name.Replace(' ', '-'));
            string keys = Path.Combine(dir, "keys");
            string work = Path.Combine(dir, "work");
            string bin = MakeFakeUsign(Path.Combine(dir, "bin"));
            // The device fetches with uclient-fetch -q -T 30 -O <file> <url>: here the "index" is made of the URL.
            File.WriteAllText(Path.Combine(bin, "uclient-fetch"), "#!/bin/sh\nprintf 'index of %s' \"$6\" > \"$5\"\n");
            Run("chmod", "+x " + Path.Combine(bin, "uclient-fetch"), null, out _, out _);
            Directory.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "stale"), "untrusted comment: " + OpkgSigning.KeyComment + "\nRWOLD\n");
            File.WriteAllText(Path.Combine(keys, "vendor"), "untrusted comment: vendor\nRWV\n");
            string Exec(string script, out int exit)
            {
                string mapped = "PATH=" + bin + ":$PATH; export PATH; " + script.Replace("/etc/opkg/keys", keys).Replace("/tmp/smartty-local-sign", work);
                exit = RunArgs(shell, args.Concat(new[] { mapped }).ToArray(), out string output, out string error);
                return output + error;
            }

            string setup = Exec(OpkgSigning.BuildSetupScript(), out int setupExit);
            string fingerprint = OpkgSigning.ParseSetup(setup.Split('\n'));
            Check(setupExit == 0 && fingerprint != null && fingerprint != OpkgSigning.NoUsign && File.Exists(Path.Combine(keys, fingerprint)) && File.Exists(Path.Combine(work, "key.sec")), name + ": the setup makes and trusts the key (" + setup.Trim() + ")");
            Check(!File.Exists(Path.Combine(keys, "stale")) && File.Exists(Path.Combine(keys, "vendor")), name + ": the setup removes the key of an earlier run and keeps the other one");
            string signed = Exec(OpkgSigning.BuildSignScript("http://127.0.0.1:1/r0/base/Packages"), out int signExit);
            byte[] signature = OpkgSigning.ParseSignature(signed.Split('\n'));
            Check(signExit == 0 && signature != null && Encoding.UTF8.GetString(signature) == "untrusted comment: signed by key test\nRWS" + Sha256Hex(Encoding.UTF8.GetBytes("index of http://127.0.0.1:1/r0/base/Packages")) + "\n", name + ": the sign script has the device fetch and sign the index (" + signed.Trim() + ")");
            Exec(OpkgSigning.BuildRemoveScript(), out int removeExit);
            Check(removeExit == 0 && !File.Exists(Path.Combine(keys, fingerprint)) && File.Exists(Path.Combine(keys, "vendor")) && !Directory.Exists(work), name + ": the remove script takes the key and the work directory away, nothing else");
            Exec(OpkgSigning.BuildRemoveScript(), out int again);
            Check(again == 0, name + ": removing when there is nothing to remove succeeds");
            string noTool = Exec(OpkgSigning.BuildSetupScript().Replace("command -v usign", "command -v no-such-usign"), out _);
            Check(OpkgSigning.ParseSetup(noTool.Split('\n')) == OpkgSigning.NoUsign, name + ": without usign the setup says so");
        }
    }

    /// <summary>The server signs a generated index through its signer, once for each version of the index.</summary>
    private static async Task SignedIndexServerTests()
    {
        string root = Path.Combine(_work, "signed", "bin", "packages", "arm");
        string feed = Path.Combine(root, "base");
        MakeIpk(feed, "one", "1.0", "First");
        List<string> asked = new List<string>();
        using (LocalRepoServer server = new LocalRepoServer(new[] { root }, 0))
        using (HttpClient client = new HttpClient())
        {
            server.Feeds = LocalRepoFeedScanner.Scan(new[] { root });
            server.Start();
            string baseUrl = "http://127.0.0.1:" + server.Port;
            Check((await GetAsync(client, baseUrl + "/r0/base/Packages.sig")).status == 404, "without a signer a generated index has no signature");
            server.IndexSigner = (string path) =>
            {
                asked.Add(path);
                return Encoding.UTF8.GetBytes("untrusted comment: test\nRWSIG" + asked.Count + "\n");
            };
            (int status, byte[] body) sig = await GetAsync(client, baseUrl + "/r0/base/Packages.sig");
            Check(sig.status == 200 && Encoding.UTF8.GetString(sig.body) == "untrusted comment: test\nRWSIG1\n", "with a signer the generated index gets its signature");
            Check(asked.Count == 1 && asked[0] == "/r0/base/Packages", "the signer is asked for the Packages file of the feed");
            Check(Encoding.UTF8.GetString((await GetAsync(client, baseUrl + "/r0/base/Packages.sig")).body).Contains("RWSIG1") && asked.Count == 1, "the same index is signed once");
            MakeIpk(feed, "two", "1.0", "Second");
            Check(Encoding.UTF8.GetString((await GetAsync(client, baseUrl + "/r0/base/Packages.sig")).body).Contains("RWSIG2") && asked.Count == 2, "a changed index is signed again");
            server.ForgetSignatures();
            await GetAsync(client, baseUrl + "/r0/base/Packages.sig");
            Check(asked.Count == 3, "after the key was replaced the index is signed again");
            server.IndexSigner = (string path) => null;
            server.ForgetSignatures();
            Check((await GetAsync(client, baseUrl + "/r0/base/Packages.sig")).status == 404, "a signer that cannot sign leaves the 404");
            server.IndexSigner = (string path) => throw new InvalidOperationException("device gone");
            Check((await GetAsync(client, baseUrl + "/r0/base/Packages.sig")).status == 404, "a signer that throws leaves the 404");
            // A fresh index of the build keeps its own signature; the signer is not used for it.
            string built = Path.Combine(_work, "signed", "bin", "packages", "arm", "luci");
            string builtIpk = MakeIpk(built, "ui", "1.0", "Built");
            File.WriteAllText(Path.Combine(built, "Packages"), "Package: ui\n\n");
            File.WriteAllText(Path.Combine(built, "Packages.sig"), "build signature");
            File.SetLastWriteTimeUtc(Path.Combine(built, "Packages"), File.GetLastWriteTimeUtc(builtIpk).AddMinutes(5));
            asked.Clear();
            server.IndexSigner = (string path) => { asked.Add(path); return new byte[] { 1 }; };
            (int status, byte[] body) own = await GetAsync(client, baseUrl + "/r0/luci/Packages.sig");
            Check(own.status == 200 && Encoding.UTF8.GetString(own.body) == "build signature" && asked.Count == 0, "the signature of the build is served as it is, not replaced");
        }
    }

    /// <summary>The host with a device that has usign: the temporary key is made and trusted, the generated index is signed by the device, Stop takes the key away.</summary>
    private static async Task SigningHostTests()
    {
        if (!Has("wget") && !Has("curl"))
        {
            Skip("signing through the device", "neither wget nor curl is installed");
            return;
        }
        string root = Path.Combine(_work, "signhost", "bin", "packages", "arm");
        string feed = Path.Combine(root, "base");
        MakeIpk(feed, "generated", "1.0", "Generated here");
        string dir = Path.Combine(_work, "signhost", "device");
        string keys = Path.Combine(dir, "keys");
        string work = Path.Combine(dir, "sign");
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, "0000000000000001"), "untrusted comment: the vendor key\nRWVENDOR\n");
        File.WriteAllText(Path.Combine(keys, "stale"), "untrusted comment: " + OpkgSigning.KeyComment + "\nRWOLD\n");
        string opkgConf = Path.Combine(dir, "opkg.conf");
        File.WriteAllText(opkgConf, "dest root /\noption check_signature\n");
        SshSession NewDevice()
        {
            SshSession s = NewFakeSession(Path.Combine(dir, "customfeeds.conf"));
            s.OpkgConfFile = opkgConf;
            s.OpkgKeysDir = keys;
            s.SignWorkDir = work;
            s.UsignPathDir = MakeFakeUsign(Path.Combine(dir, "bin"));
            s.RunFetchCheck = true;
            return s;
        }

        LocalRepoHost host = new LocalRepoHost();
        SshSession session = NewDevice();
        host.Start(session, new[] { root }, 0, LocalRepoPackageManager.Opkg, signGenerated: true);
        string[] trusted = Directory.GetFiles(keys).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Check(trusted.Length == 2 && trusted.Contains("0000000000000001") && !trusted.Contains("stale"), "the temporary key is trusted, a key of an earlier run is gone, the vendor key stays");
        string ourKey = trusted.First(n => n != "0000000000000001");
        Check(File.ReadAllText(Path.Combine(keys, ourKey)).Contains(OpkgSigning.KeyComment) && ourKey.Length == 16, "the key file is named by the fingerprint and carries the comment of SmarTTY");
        Check(File.Exists(Path.Combine(work, "key.sec")), "the secret half stays in the work directory of the device");
        Check(host.GetHistory().Any(l => l.Contains("is generated here and has no signature of the build") && l.Contains("temporary key (" + ourKey + ")")) && !host.GetHistory().Any(l => l.Contains("Warning: opkg checks signatures")), "the log says the index is signed with the temporary key, and gives no warning about unsigned feeds");
        using (HttpClient client = new HttpClient())
        {
            string baseUrl = "http://127.0.0.1:" + host.Port;
            (int status, byte[] body) index = await GetAsync(client, baseUrl + "/r0/base/Packages");
            (int status, byte[] body) sig = await GetAsync(client, baseUrl + "/r0/base/Packages.sig");
            Check(sig.status == 200 && Encoding.UTF8.GetString(sig.body) == "untrusted comment: signed by key test\nRWS" + Sha256Hex(index.body) + "\n", "Packages.sig is what the device signed: its own copy of exactly the served Packages");
        }
        host.Stop(session);
        Check(Directory.GetFiles(keys).Select(Path.GetFileName).SequenceEqual(new[] { "0000000000000001" }) && !Directory.Exists(work), "Stop removes the temporary key and the work directory and leaves the other keys");

        // The check is off for opkg, or unsigned feeds are allowed: no key is made.
        host.Start(session, new[] { root }, 0, LocalRepoPackageManager.Opkg, allowUnsigned: true, signGenerated: true);
        Check(Directory.GetFiles(keys).Length == 1 && !Directory.Exists(work), "with unsigned feeds allowed no key is made");
        host.Stop(session);

        // No usign on the device: the old warning, and the index stays unsigned.
        SshSession bare = NewDevice();
        bare.UsignPathDir = Path.Combine(dir, "empty");
        Directory.CreateDirectory(bare.UsignPathDir);
        bare.RunShell = (string script) =>
        {
            int exit = RunArgs("sh", new[] { "-c", script.Replace("command -v usign", "command -v no-such-usign") }, out string output, out string error);
            return exit == 0 ? output : null;
        };
        LocalRepoHost without = new LocalRepoHost();
        without.Start(bare, new[] { root }, 0, LocalRepoPackageManager.Opkg, signGenerated: true);
        Check(without.GetHistory().Any(l => l.Contains("no usign")) && without.GetHistory().Any(l => l.Contains("Warning: opkg checks signatures") && l.Contains("smartty_arm_base")), "a device without usign: the reason and the warning about the unsigned feed");
        using (HttpClient client = new HttpClient())
        {
            Check((await GetAsync(client, "http://127.0.0.1:" + without.Port + "/r0/base/Packages.sig")).status == 404, "and the generated index has no signature");
        }
        without.Stop(bare);

        // Reattach after the device restarted (its /tmp is empty): a new pair is made and the index is signed with it.
        LocalRepoHost again = new LocalRepoHost();
        SshSession before = NewDevice();
        again.Start(before, new[] { root }, 0, LocalRepoPackageManager.Opkg, signGenerated: true);
        string firstKey = Directory.GetFiles(keys).Select(Path.GetFileName).First(n => n != "0000000000000001");
        Directory.Delete(work, true);
        SshSession after = NewDevice();
        again.Reattach(after);
        Check(Directory.Exists(work) && File.Exists(Path.Combine(work, "key.sec")) && Directory.GetFiles(keys).Length == 2, "Reattach makes the key pair again on the restarted device");
        using (HttpClient client = new HttpClient())
        {
            Check((await GetAsync(client, "http://127.0.0.1:" + again.Port + "/r0/base/Packages.sig")).status == 200, "and the index is signed again");
        }
        again.Stop(after);
        Check(Directory.GetFiles(keys).Length == 1, "the last Stop leaves only the vendor key");
    }

    /// <summary>The host with the signature check of opkg on the device, and the check that the device reaches the server.</summary>
    private static void SignatureHostTests()
    {
        string root = Path.Combine(_work, "sighost", "bin", "packages", "arm");
        string feed = Path.Combine(root, "base");
        MakeIpk(feed, "unsigned", "1.0", "No signature");
        string dir = Path.Combine(_work, "sighost", "device");
        Directory.CreateDirectory(dir);
        string conf = Path.Combine(dir, "customfeeds.conf");
        string opkgConf = Path.Combine(dir, "opkg.conf");
        const string original = "dest root /\noption check_signature\n";

        // The check is on and unsigned feeds are not allowed: the feed is added, with a warning that opkg drops it.
        File.WriteAllText(opkgConf, original);
        LocalRepoHost host = new LocalRepoHost();
        SshSession session = NewFakeSession(conf);
        session.OpkgConfFile = opkgConf;
        session.RunFetchCheck = Has("wget") || Has("curl");
        host.Start(session, new[] { root }, 0);
        Check(File.ReadAllText(opkgConf) == original && host.GetHistory().Any(l => l.Contains("Warning: opkg checks signatures") && l.Contains("smartty_arm_base") && l.Contains("Allow unsigned feeds")), "check on: the feed without a signature is named in a warning, opkg.conf is left alone");
        if (session.RunFetchCheck)
        {
            Check(host.GetHistory().Any(l => l.Contains("The device reaches the server at http://127.0.0.1:" + host.Port + "/")), "the device fetches the start page of the server through the tunnel");
        }
        else
        {
            Skip("the device reaches the server", "neither wget nor curl is installed");
        }
        host.Stop(session);
        Check(!host.GetHistory().Last().Contains("signatures again"), "Stop without the check turned off does not claim to turn it on");

        // Allowed: the check is off while the server runs and on again after Stop.
        host.Start(session, new[] { root }, 0, LocalRepoPackageManager.Opkg, allowUnsigned: true);
        Check(File.ReadAllText(opkgConf).Contains(OpkgFeedConfig.SignatureOffMarker + "option check_signature") && host.GetHistory().Any(l => l.Contains("is commented out until the server is stopped")), "unsigned feeds allowed: the check is commented out and the log says so");
        SshSession next = NewFakeSession(conf);
        next.OpkgConfFile = opkgConf;
        File.WriteAllText(opkgConf, original);
        host.Reattach(next);
        Check(File.ReadAllText(opkgConf).Contains(OpkgFeedConfig.SignatureOffMarker), "Reattach turns the check off again (the device rebooted meanwhile)");
        host.Stop(next);
        Check(File.ReadAllText(opkgConf) == original && host.GetHistory().Last().Contains("opkg checks signatures again"), "Stop turns the check on again and says so");

        // A fresh index with its signature is fine for opkg: no warning.
        File.WriteAllText(Path.Combine(feed, "Packages"), "Package: unsigned\n\n");
        File.WriteAllText(Path.Combine(feed, "Packages.sig"), "untrusted comment: signed by key 0123456789abcdef\nRWQ=\n");
        File.SetLastWriteTimeUtc(Path.Combine(feed, "Packages"), DateTime.UtcNow.AddMinutes(5));
        Check(LocalRepoHost.DescribeUnsignedFeeds(LocalRepoFeedScanner.Scan(new[] { root })) == null, "a fresh index with Packages.sig needs no warning");
        File.SetLastWriteTimeUtc(Path.Combine(feed, "Packages"), DateTime.UtcNow.AddHours(-1));
        Check(LocalRepoHost.DescribeUnsignedFeeds(LocalRepoFeedScanner.Scan(new[] { root })) != null, "a stale index is generated and has no signature: warning");

        // The tunnel leads nowhere: the warning tells what to check.
        LocalRepoHost broken = new LocalRepoHost();
        SshSession nowhere = NewFakeSession(conf);
        nowhere.RunFetchCheck = true;
        nowhere.RunShell = (string script) => script.Contains("SMARTTY_FETCH") ? "SMARTTY_FETCH=failed\n" : "";
        broken.Start(nowhere, new[] { root }, 0);
        Check(broken.GetHistory().Any(l => l.Contains("Warning: the device could not fetch") && l.Contains("TCP forwarding")), "a device that cannot reach the server gets a warning in the log");
        broken.Stop(nowhere);
    }

    /// <summary>Writes an "ar" archive like opkg-build of opkg-utils makes (and dpkg-deb): debian-binary, control.tar.gz, data.tar.gz.</summary>
    private static string MakeArIpk(string directory, string name, string version, bool gnuNames, bool oddSizes)
    {
        string staging = Path.Combine(_work, "arstage-" + Guid.NewGuid().ToString("N"));
        string ctl = Path.Combine(staging, "ctl");
        Directory.CreateDirectory(ctl);
        File.WriteAllText(Path.Combine(ctl, "control"), "Package: " + name + "\nVersion: " + version + "\nArchitecture: all\nDescription: From an ar archive\n");
        Check(Run("tar", "-czf control.tar.gz -C ctl ./control", staging, out _, out string e1) == 0, "tar control.tar.gz " + e1);
        byte[] control = File.ReadAllBytes(Path.Combine(staging, "control.tar.gz"));
        byte[] data = new byte[oddSizes ? 1001 : 1000];
        string ipk = Path.Combine(directory, name + "_" + version + "_all.ipk");
        Directory.CreateDirectory(directory);
        using (FileStream output = File.Create(ipk))
        {
            void Member(string memberName, byte[] content)
            {
                string header = (memberName + (gnuNames ? "/" : "")).PadRight(16) + "0".PadRight(12) + "0".PadRight(6) + "0".PadRight(6) + "100644".PadRight(8) + content.Length.ToString().PadRight(10) + "`\n";
                output.Write(Encoding.ASCII.GetBytes(header));
                output.Write(content);
                if (content.Length % 2 != 0)
                {
                    output.WriteByte((byte)'\n');
                }
            }
            output.Write(Encoding.ASCII.GetBytes("!<arch>\n"));
            Member("debian-binary", Encoding.ASCII.GetBytes(oddSizes ? "2.0\n\n\n" : "2.0\n"));
            Member("control.tar.gz", control);
            Member("data.tar.gz", data);
        }
        return ipk;
    }

    private static void ArIpkTests()
    {
        string dir = Path.Combine(_work, "ar-feed");
        string plain = MakeArIpk(dir, "fromar", "1.0", gnuNames: false, oddSizes: false);
        string gnu = MakeArIpk(dir, "fromgnuar", "2.0", gnuNames: true, oddSizes: true);
        MakeIpk(dir, "fromtar", "3.0", "OpenWrt style");
        Check(IpkIndexBuilder.ReadControl(plain)?.Contains("Package: fromar") == true, "the control file of an .ipk in ar format is read");
        Check(IpkIndexBuilder.ReadControl(gnu)?.Contains("Package: fromgnuar") == true, "GNU ar member names (with '/') and odd member sizes are read");
        IpkIndexBuilder.Index index = IpkIndexBuilder.Build(dir);
        string text = Encoding.UTF8.GetString(index.Packages);
        Check(index.Count == 3 && index.Warnings.Count == 0 && text.Contains("Package: fromar") && text.Contains("Package: fromgnuar") && text.Contains("Package: fromtar"), "ar and tar packages are indexed side by side");
        Check(text.Contains("Filename: fromar_1.0_all.ipk\nSize: " + new FileInfo(plain).Length), "the file fields of an ar package");
        if (Has("ar"))
        {
            string arDir = Path.Combine(_work, "ar-tool");
            Directory.CreateDirectory(arDir);
            File.Copy(plain, Path.Combine(arDir, "copy.ipk"));
            Check(Run("ar", "t copy.ipk", arDir, out string members, out _) == 0 && members.Replace("\r", "") == "debian-binary\ncontrol.tar.gz\ndata.tar.gz\n", "the test archive is a valid ar archive for the system ar");
        }
        File.WriteAllBytes(Path.Combine(dir, "broken_1.0_all.ipk"), Encoding.ASCII.GetBytes("!<arch>\nnot a header at all"));
        IpkIndexBuilder.Index withBroken = IpkIndexBuilder.Build(dir);
        Check(withBroken.Count == 3 && withBroken.Warnings.Count == 1 && withBroken.Warnings[0].StartsWith("broken_1.0_all.ipk"), "a damaged ar archive is skipped with a warning");
    }

    private static bool PortIsOpen(int port)
    {
        try
        {
            using (new TcpClient("127.0.0.1", port))
            {
                return true;
            }
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the probe of <see cref="OpenWrtDetector"/> under the given shell against a fake root: the probe looks for
    /// /etc/openwrt_release and /etc/opkg, so the whole filesystem test is done with a prefix that is swapped in.
    /// </summary>
    private static bool RunProbe(string shell, string[] args, string etcDir, bool opkgOnPath)
    {
        string fakeBin = Path.Combine(_work, "probebin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeBin);
        if (opkgOnPath)
        {
            File.WriteAllText(Path.Combine(fakeBin, "opkg"), "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(Path.Combine(fakeBin, "opkg"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        LinkGrep(fakeBin);
        string script = "PATH=" + fakeBin + "; " + OpenWrtDetector.ProbeCommand.Replace("/etc/", etcDir + "/");
        int exit = RunArgs(shell, args.Concat(new[] { script }).ToArray(), out string output, out string error);
        if (exit != 0)
        {
            throw new Exception("the probe command failed: " + output + error);
        }
        return OpenWrtDetector.Parse((output + error).Split('\n'));
    }

    /// <summary>The probe reads /etc/os-release with grep, the only program it needs besides the shell.</summary>
    private static void LinkGrep(string fakeBin)
    {
        string grep = new[] { "/usr/bin/grep", "/bin/grep" }.FirstOrDefault(File.Exists);
        if (grep != null)
        {
            File.CreateSymbolicLink(Path.Combine(fakeBin, "grep"), grep);
        }
    }

    private static void DetectorTests()
    {
        List<(string name, string shell, string[] args)> shells = new List<(string, string, string[])> { ("sh", "sh", new[] { "-c" }) };
        if (Has("busybox"))
        {
            shells.Add(("busybox ash", "busybox", new[] { "ash", "-c" }));
        }
        foreach ((string name, string shell, string[] args) in shells)
        {
            string openwrtEtc = Path.Combine(_work, "etc-openwrt-" + name.Replace(' ', '-'));
            string plainEtc = Path.Combine(_work, "etc-plain-" + name.Replace(' ', '-'));
            string opkgEtc = Path.Combine(_work, "etc-opkgonly-" + name.Replace(' ', '-'));
            Directory.CreateDirectory(openwrtEtc);
            Directory.CreateDirectory(plainEtc);
            Directory.CreateDirectory(Path.Combine(opkgEtc, "opkg"));
            File.WriteAllText(Path.Combine(openwrtEtc, "openwrt_release"), "DISTRIB_ID='OpenWrt'\n");

            Check(RunProbe(shell, args, openwrtEtc, opkgOnPath: true), name + ": OpenWrt with opkg is detected");
            Check(!RunProbe(shell, args, openwrtEtc, opkgOnPath: false), name + ": OpenWrt without opkg is not offered");
            Check(!RunProbe(shell, args, plainEtc, opkgOnPath: true), name + ": a plain host with opkg installed is not offered");
            Check(!RunProbe(shell, args, plainEtc, opkgOnPath: false), name + ": a plain host is not offered");
            Check(RunProbe(shell, args, opkgEtc, opkgOnPath: true), name + ": the opkg configuration directory alone counts as OpenWrt");

            // Derivatives tell about their OpenWrt base in /etc/os-release only.
            string derivativeEtc = Path.Combine(_work, "etc-immortalwrt-" + name.Replace(' ', '-'));
            Directory.CreateDirectory(derivativeEtc);
            File.WriteAllText(Path.Combine(derivativeEtc, "os-release"), "NAME=\"ImmortalWrt\"\nID=\"immortalwrt\"\nID_LIKE=\"lede openwrt\"\n");
            Check(RunProbe(shell, args, derivativeEtc, opkgOnPath: true), name + ": a derivative with ID_LIKE=openwrt in os-release is detected");
            string openwrtOsRelease = Path.Combine(_work, "etc-osrelease-" + name.Replace(' ', '-'));
            Directory.CreateDirectory(openwrtOsRelease);
            File.WriteAllText(Path.Combine(openwrtOsRelease, "os-release"), "NAME=\"OpenWrt\"\nID=\"openwrt\"\n");
            Check(RunProbe(shell, args, openwrtOsRelease, opkgOnPath: true), name + ": ID=openwrt in os-release is detected");
            string debianEtc = Path.Combine(_work, "etc-debian-" + name.Replace(' ', '-'));
            Directory.CreateDirectory(debianEtc);
            File.WriteAllText(Path.Combine(debianEtc, "os-release"), "NAME=\"Debian GNU/Linux\"\nID=debian\nHOME_URL=\"https://openwrt.org/\"\n");
            Check(!RunProbe(shell, args, debianEtc, opkgOnPath: true), name + ": another system in os-release is not taken for OpenWrt, even if the word appears elsewhere");
        }

        Check(!OpenWrtDetector.Parse(null), "probe output: none means not OpenWrt");
        Check(!OpenWrtDetector.Parse(new[] { "" }), "probe output: empty means not OpenWrt");
        Check(!OpenWrtDetector.Parse(new[] { OpenWrtDetector.OpenWrtMarker }), "probe output: OpenWrt alone is not enough");
        Check(!OpenWrtDetector.Parse(new[] { OpenWrtDetector.OpkgMarker }), "probe output: opkg alone is not enough");
        Check(OpenWrtDetector.Parse(new[] { OpenWrtDetector.OpenWrtMarker, OpenWrtDetector.OpkgMarker }), "probe output: both markers mean OpenWrt with opkg");
        Check(!OpenWrtDetector.Parse(new[] { "SMARTTY_OS=other", OpenWrtDetector.OpkgMarker }), "probe output: another OS is refused");
        Check(OpenWrtDetector.Parse(new[] { "  " + OpenWrtDetector.OpenWrtMarker + "  ", OpenWrtDetector.OpkgMarker + "\r" }), "probe output: surrounding blanks and CR are ignored");
        Check(!OpenWrtDetector.Parse(new[] { "sh: opkg: not found" }), "probe output: an error line is not a positive answer");
    }

    private static SshSession NewFakeSession(string conf)
    {
        SshSession session = new SshSession { LocalConfigFile = conf };
        session.RunShell = (string script) =>
        {
            int exit = RunArgs("sh", new[] { "-c", script }, out string output, out string error);
            return exit == 0 ? output : null;
        };
        return session;
    }

    private static async Task HostTests()
    {
        string root = Path.Combine(_work, "host", "bin", "packages", "arm");
        string feed = Path.Combine(root, "base");
        string ipk = MakeIpk(feed, "viahost", "1.0", "Installed through the host");
        string conf = Path.Combine(_work, "host", "customfeeds.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(conf));
        File.WriteAllText(conf, "# the user's own feed\nsrc/gz mine http://example.com/feed\n");

        // Normal start and stop.
        LocalRepoHost host = new LocalRepoHost();
        SshSession session = NewFakeSession(conf);
        host.Start(session, new[] { root }, 0);
        int port = host.Port;
        Check(host.IsRunning && port > 0 && host.Feeds.Count == 1 && host.IsTunnelCurrent(session), "Start runs the server, the tunnel is for this session");
        Check(session.TunnelPort == (uint)port, "the tunnel forwards the port of the server to itself");
        string started = File.ReadAllText(conf);
        Check(started == "# the user's own feed\nsrc/gz mine http://example.com/feed\n# smartty-local-begin\nsrc/gz smartty_arm_base http://127.0.0.1:" + port + "/r0/base\n# smartty-local-end\n", "Start adds the feeds to the configuration of the device and keeps the user's lines");
        Check(session.Commands.IndexOf("command -v opkg >/dev/null 2>&1") < session.Commands.FindIndex(c => c.Contains("smartty-local-begin")), "opkg is checked before the configuration is changed");
        using (HttpClient client = new HttpClient())
        {
            (int status, byte[] body) response = await GetAsync(client, "http://127.0.0.1:" + port + "/r0/base/Packages");
            Check(response.status == 200 && Encoding.UTF8.GetString(response.body).Contains("Package: viahost"), "the feed of the device's configuration is served");
        }
        List<string> lines = new List<string>();
        host.Log += (string line) => { lock (lines) { lines.Add(line); } };
        bool second = false;
        try
        {
            host.Start(session, new[] { root }, 0);
        }
        catch (InvalidOperationException)
        {
            second = true;
        }
        Check(second, "starting a running host is refused");
        host.Report("hello");
        Check(host.GetHistory().Any(l => l.Contains("Server started on port " + port)) && host.GetHistory().Any(l => l.EndsWith("hello") && l.Length > "hello".Length + 8) && lines.Any(l => l.EndsWith("hello")), "the history keeps time-stamped lines and listeners get them");

        host.Stop(session);
        Check(!host.IsRunning && host.Port == 0 && session.TunnelPort == null && session.TunnelStops == 1, "Stop closes the tunnel");
        Check(File.ReadAllText(conf) == "# the user's own feed\nsrc/gz mine http://example.com/feed\n", "Stop removes the feeds from the device");
        Check(!PortIsOpen(port), "Stop closes the server");
        host.Stop(session);
        Check(session.TunnelStops == 1, "a second Stop does nothing");

        // Refusals that must leave nothing behind.
        LocalRepoHost refused = new LocalRepoHost();
        SshSession plain = NewFakeSession(conf);
        Check(Throws<InvalidOperationException>(() => refused.Start(plain, new string[0], 0)) && !refused.IsRunning, "no folder: refused");
        Check(Throws<DirectoryNotFoundException>(() => refused.Start(plain, new[] { Path.Combine(_work, "nope") }, 0)), "a missing folder: refused");
        string emptyFeeds = Path.Combine(_work, "host", "empty");
        Directory.CreateDirectory(emptyFeeds);
        Check(Throws<InvalidOperationException>(() => refused.Start(plain, new[] { emptyFeeds }, 0)), "no feeds in the folders: refused");
        SshSession noOpkg = NewFakeSession(conf);
        noOpkg.OpkgMissing = true;
        Check(Throws<InvalidOperationException>(() => refused.Start(noOpkg, new[] { root }, 0)) && noOpkg.TunnelStarts == 0 && File.ReadAllText(conf).Contains("smartty") == false, "no opkg on the device: refused before anything is changed");

        TcpListener busy = new TcpListener(System.Net.IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            int busyPort = ((System.Net.IPEndPoint)busy.LocalEndpoint).Port;
            SshSession onBusy = NewFakeSession(conf);
            InvalidOperationException busyError = Catch<InvalidOperationException>(() => refused.Start(onBusy, new[] { root }, busyPort));
            Check(busyError != null && busyError.Message.Contains("port " + busyPort) && onBusy.TunnelStarts == 0, "a busy local port: refused with a message, no tunnel");
        }
        finally
        {
            busy.Stop();
        }

        SshSession noTunnel = NewFakeSession(conf);
        noTunnel.FailTunnel = true;
        InvalidOperationException tunnelError = Catch<InvalidOperationException>(() => refused.Start(noTunnel, new[] { root }, 0));
        Check(tunnelError != null && tunnelError.Message.Contains("reverse tunnel") && tunnelError.Message.Contains("administratively prohibited") && !refused.IsRunning && !File.ReadAllText(conf).Contains("smartty"), "the tunnel is refused by the device: message, nothing configured");

        SshSession noConfig = NewFakeSession(conf);
        noConfig.FailEnable = true;
        Check(Throws<InvalidOperationException>(() => refused.Start(noConfig, new[] { root }, 0)) && noConfig.TunnelPort == null && noConfig.TunnelStops == 1 && !refused.IsRunning, "the configuration cannot be written: the tunnel is closed again");

        // Not root: the scripts go through sudo.
        LocalRepoHost sudoHost = new LocalRepoHost();
        SshSession user = NewFakeSession(conf);
        user.IsRootUser = false;
        user.LocalConfigFile = null;
        sudoHost.Start(user, new[] { root }, 0);
        Check(user.Commands.Any(c => c.StartsWith("sudo -n sh -c ") && c.Contains("smartty-local-begin")), "a non-root user goes through sudo -n");
        sudoHost.Stop(user);
        Check(user.Commands.Count(c => c.StartsWith("sudo -n sh -c ") && c.Contains("sed -i")) == 2, "and the feeds are removed through sudo -n as well");

        // The connection is replaced: the tunnel and the feeds come back on the new one.
        LocalRepoHost reconnecting = new LocalRepoHost();
        SshSession first = NewFakeSession(conf);
        reconnecting.Start(first, new[] { root }, 0);
        int reconnectingPort = reconnecting.Port;
        SshSession next = NewFakeSession(conf);
        File.WriteAllText(conf, "# wiped by a reboot of the device\n");
        reconnecting.Reattach(next);
        Check(reconnecting.IsTunnelCurrent(next) && !reconnecting.IsTunnelCurrent(first) && next.TunnelPort == (uint)reconnectingPort, "Reattach moves the tunnel to the new connection");
        Check(File.ReadAllText(conf).Contains("http://127.0.0.1:" + reconnectingPort + "/r0/base"), "Reattach writes the feeds again");
        reconnecting.Reattach(next);
        Check(next.TunnelStarts == 1, "Reattach with the current connection does nothing");
        reconnecting.Stop(null);
        Check(!reconnecting.IsRunning && !PortIsOpen(reconnectingPort), "Stop without a connection still stops the server");
        Check(reconnecting.GetHistory().Any(l => l.Contains("no SSH connection")), "and says that the feeds could not be removed");
        Check(reconnecting.RemoveFeeds(null).Contains("no SSH connection"), "RemoveFeeds without a connection reports it");
        File.WriteAllText(conf, "x\n# smartty-local-begin\nsrc/gz leftover http://127.0.0.1:1/r0/a\n# smartty-local-end\ny\n");
        Check(reconnecting.RemoveFeeds(NewFakeSession(conf)) == null && File.ReadAllText(conf) == "x\ny\n", "RemoveFeeds cleans up leftovers of an earlier run");

        // Settings are saved per computer.
        string appData = Path.Combine(_work, "appdata");
        Directory.CreateDirectory(appData);
        string previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", appData);
        try
        {
            Check(LocalRepoSettings.Load().Port == LocalRepoSettings.DefaultPort && LocalRepoSettings.Load().Folders.Count == 0, "settings: defaults without a file");
            LocalRepoSettings saved = new LocalRepoSettings { Port = 9123 };
            saved.Folders.Add(root);
            saved.Folders.Add(feed);
            saved.Save();
            LocalRepoSettings loaded = LocalRepoSettings.Load();
            Check(loaded.Port == 9123 && loaded.Folders.SequenceEqual(new[] { root, feed }), "settings: folders and port survive a restart");
            File.WriteAllText(Directory.GetFiles(appData, "*LocalRepo.xml", SearchOption.AllDirectories)[0], "<LocalRepoSettings><Port>70000</Port></LocalRepoSettings>");
            Check(LocalRepoSettings.Load().Port == LocalRepoSettings.DefaultPort, "settings: an invalid port falls back to the default");
            File.WriteAllText(Directory.GetFiles(appData, "*LocalRepo.xml", SearchOption.AllDirectories)[0], "not xml at all");
            Check(LocalRepoSettings.Load().Folders.Count == 0, "settings: a damaged file gives the defaults");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }
    }

    private static T Catch<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }
        return null;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        return Catch<T>(action) != null;
    }

    private static string ExecShell(string shell, string[] args, string script, out int exit)
    {
        exit = RunArgs(shell, args.Concat(new[] { script }).ToArray(), out string output, out string error);
        return output + error;
    }

    private static List<(string name, string shell, string[] args)> Shells()
    {
        List<(string name, string shell, string[] args)> shells = new List<(string, string, string[])> { ("sh", "sh", new[] { "-c" }) };
        if (Has("busybox"))
        {
            shells.Add(("busybox ash", "busybox", new[] { "ash", "-c" }));
        }
        return shells;
    }

    private static void MakeFakeProgram(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static LocalRepoPackageManager? RunProbeDetect(string shell, string[] args, string etcDir, bool opkgOnPath, bool apkOnPath)
    {
        string fakeBin = Path.Combine(_work, "probebin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeBin);
        if (opkgOnPath)
        {
            MakeFakeProgram(fakeBin, "opkg");
        }
        if (apkOnPath)
        {
            MakeFakeProgram(fakeBin, "apk");
        }
        LinkGrep(fakeBin);
        string script = "PATH=" + fakeBin + "; " + OpenWrtDetector.ProbeCommand.Replace("/etc/", etcDir + "/");
        int exit = RunArgs(shell, args.Concat(new[] { script }).ToArray(), out string output, out string error);
        if (exit != 0)
        {
            throw new Exception("the probe command failed: " + output + error);
        }
        return OpenWrtDetector.Detect((output + error).Split('\n'));
    }

    private static string FindApk()
    {
        foreach (string candidate in new[] { Environment.GetEnvironmentVariable("SMARTTY_APK"), "apk", "/tmp/apk-tools/build/src/apk" })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }
            try
            {
                if (Run(candidate, "--version", null, out string output, out _) == 0 && output.Contains("apk-tools 3"))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private const string TestPublicKey = "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAtestkeytestkeytestkey\n-----END PUBLIC KEY-----";

    private static void ApkDetectorAndScannerTests()
    {
        // Detection of the package manager from the probe output.
        Check(OpenWrtDetector.Detect(new[] { OpenWrtDetector.OpenWrtMarker, OpenWrtDetector.ApkMarker }) == LocalRepoPackageManager.Apk, "probe output: OpenWrt with apk");
        Check(OpenWrtDetector.Detect(new[] { OpenWrtDetector.OpenWrtMarker, OpenWrtDetector.OpkgMarker }) == LocalRepoPackageManager.Opkg, "probe output: OpenWrt with opkg");
        Check(OpenWrtDetector.Detect(new[] { OpenWrtDetector.OpenWrtMarker, OpenWrtDetector.OpkgMarker, OpenWrtDetector.ApkMarker }) == LocalRepoPackageManager.Apk, "probe output: apk wins when both are installed");
        Check(OpenWrtDetector.Detect(new[] { OpenWrtDetector.OpenWrtMarker, "SMARTTY_PKG=none" }) == null, "probe output: OpenWrt without a package manager is not offered");
        Check(OpenWrtDetector.Detect(new[] { "SMARTTY_OS=other", OpenWrtDetector.ApkMarker }) == null, "probe output: apk on another OS (Alpine) is not offered");
        Check(OpenWrtDetector.Detect(null) == null && OpenWrtDetector.Parse(new[] { OpenWrtDetector.OpenWrtMarker, OpenWrtDetector.ApkMarker }), "probe output: Parse accepts apk too");

        foreach ((string name, string shell, string[] args) in Shells())
        {
            string tag = name.Replace(' ', '-');
            string openwrtEtc = Path.Combine(_work, "apk-etc-openwrt-" + tag);
            string listOnlyEtc = Path.Combine(_work, "apk-etc-listonly-" + tag);
            string alpineEtc = Path.Combine(_work, "apk-etc-alpine-" + tag);
            Directory.CreateDirectory(openwrtEtc);
            Directory.CreateDirectory(Path.Combine(listOnlyEtc, "apk", "repositories.d"));
            Directory.CreateDirectory(Path.Combine(alpineEtc, "apk"));
            File.WriteAllText(Path.Combine(openwrtEtc, "openwrt_release"), "DISTRIB_ID='OpenWrt'\n");
            File.WriteAllText(Path.Combine(listOnlyEtc, "apk", "repositories.d", "customfeeds.list"), "");
            File.WriteAllText(Path.Combine(alpineEtc, "apk", "repositories"), "https://dl-cdn.alpinelinux.org/alpine/v3.20/main\n");

            Check(RunProbeDetect(shell, args, openwrtEtc, opkgOnPath: false, apkOnPath: true) == LocalRepoPackageManager.Apk, name + ": OpenWrt with apk is detected");
            Check(RunProbeDetect(shell, args, openwrtEtc, opkgOnPath: true, apkOnPath: false) == LocalRepoPackageManager.Opkg, name + ": OpenWrt with opkg is still detected");
            Check(RunProbeDetect(shell, args, openwrtEtc, opkgOnPath: true, apkOnPath: true) == LocalRepoPackageManager.Apk, name + ": apk is preferred when both are installed");
            Check(RunProbeDetect(shell, args, openwrtEtc, opkgOnPath: false, apkOnPath: false) == null, name + ": OpenWrt without opkg and apk is not offered");
            Check(RunProbeDetect(shell, args, listOnlyEtc, opkgOnPath: false, apkOnPath: true) == LocalRepoPackageManager.Apk, name + ": the custom apk repository list alone counts as OpenWrt");
            Check(RunProbeDetect(shell, args, alpineEtc, opkgOnPath: false, apkOnPath: true) == null, name + ": Alpine Linux (apk without the OpenWrt files) is not offered");
        }

        // Feeds of both kinds in one tree.
        string root = Path.Combine(_work, "kinds", "bin", "packages", "x86_64");
        Directory.CreateDirectory(Path.Combine(root, "base"));
        File.WriteAllText(Path.Combine(root, "base", "packages.adb"), "adb");
        File.WriteAllText(Path.Combine(root, "base", "a-1.0-r1.apk"), "apk");
        Directory.CreateDirectory(Path.Combine(root, "late"));
        File.WriteAllText(Path.Combine(root, "late", "b-1.0-r1.apk"), "apk without an index");
        MakeIpk(Path.Combine(root, "ipk"), "foo", "1.0", "An opkg feed");
        Directory.CreateDirectory(Path.Combine(root, "mixed"));
        File.WriteAllText(Path.Combine(root, "mixed", "packages.adb"), "adb");
        File.WriteAllText(Path.Combine(root, "mixed", "Packages"), "");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "empty", "index.json"), "{}");
        List<LocalRepoFeed> feeds = LocalRepoFeedScanner.Scan(new[] { root });
        Check(feeds.Select(f => Path.GetFileName(f.Directory)).SequenceEqual(new[] { "base", "ipk", "late", "mixed" }), "scanner finds apk feeds (packages.adb or .apk files) next to opkg feeds");
        LocalRepoFeed Find(string name) => feeds.First(f => Path.GetFileName(f.Directory) == name);
        Check(Find("base").HasApk && !Find("base").HasOpkg && Find("late").HasApk && !Find("late").HasOpkg, "a folder with packages.adb or .apk files is an apk feed");
        Check(Find("ipk").HasOpkg && !Find("ipk").HasApk, "a folder with .ipk files is an opkg feed");
        Check(Find("mixed").HasOpkg && Find("mixed").HasApk, "a folder with both indexes is a feed of both kinds");
        Check(feeds.Count(f => f.IsFor(LocalRepoPackageManager.Apk)) == 3 && feeds.Count(f => f.IsFor(LocalRepoPackageManager.Opkg)) == 2, "IsFor picks the feeds of a package manager");

        string stale = Path.Combine(_work, "kinds-stale");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "packages.adb"), "adb");
        string older = Path.Combine(stale, "old-1.0-r1.apk");
        string newer = Path.Combine(stale, "new-1.0-r1.apk");
        File.WriteAllText(older, "x");
        File.WriteAllText(newer, "x");
        DateTime indexTime = File.GetLastWriteTimeUtc(Path.Combine(stale, "packages.adb"));
        File.SetLastWriteTimeUtc(older, indexTime.AddMinutes(-5));
        File.SetLastWriteTimeUtc(newer, indexTime.AddMinutes(5));
        Check(ApkFeedInfo.CountPackagesNewerThanIndex(stale) == 1, "ApkFeedInfo counts the .apk files newer than the index");
        Check(ApkFeedInfo.CountPackagesNewerThanIndex(Path.Combine(_work, "kinds-nope")) == 0 && ApkFeedInfo.CountPackagesNewerThanIndex(Path.Combine(root, "late")) == 0, "no index: nothing is reported");

        // The public key of the build tree is found above the feeds.
        string tree = Path.Combine(_work, "tree");
        string deepFeed = Path.Combine(tree, "bin", "packages", "x86_64", "base");
        Directory.CreateDirectory(deepFeed);
        Check(ApkFeedConfig.FindBuildPublicKey(new[] { deepFeed }) == null, "no public key: null");
        File.WriteAllText(Path.Combine(tree, "public-key.pem"), "not a key at all");
        Check(ApkFeedConfig.FindBuildPublicKey(new[] { deepFeed }) == null, "a public-key.pem that is not a public key is ignored");
        File.WriteAllText(Path.Combine(tree, "public-key.pem"), TestPublicKey.Replace("\n", "\r\n") + "\r\n");
        Check(ApkFeedConfig.FindBuildPublicKey(new[] { deepFeed }) == TestPublicKey, "the key is found four levels up and normalized to LF without trailing blanks");
        Check(ApkFeedConfig.FindBuildPublicKey(new[] { Path.Combine(_work, "elsewhere"), tree }) == TestPublicKey, "several start folders are tried");
    }

    private static void ApkConfigTests()
    {
        List<LocalRepoFeed> feeds = new List<LocalRepoFeed>
        {
            new LocalRepoFeed { Name = "smartty_base", UrlPath = "/r0/base", Directory = "/x", HasApk = true },
            new LocalRepoFeed { Name = "smartty_luci", UrlPath = "/r0/lu%20ci", Directory = "/y", HasApk = true }
        };
        Check(ApkFeedConfig.FeedLine(feeds[0], 8765) == "http://127.0.0.1:8765/r0/base/packages.adb", "apk repository line is the URL of packages.adb");

        foreach ((string name, string shell, string[] args) in Shells())
        {
            string dir = Path.Combine(_work, "apkconf-" + name.Replace(' ', '-'));
            string list = Path.Combine(dir, "repositories.d", "customfeeds.list");
            string key = Path.Combine(dir, "keys", "smartty-local.pem");
            string fakeBin = Path.Combine(dir, "bin");
            string emptyBin = Path.Combine(dir, "empty");
            MakeFakeProgram(fakeBin, "apk");
            Directory.CreateDirectory(emptyBin);
            Directory.CreateDirectory(Path.Combine(dir, "keys"));

            ExecShell(shell, args, "PATH=" + fakeBin + "; " + ApkFeedConfig.BuildCheckScript(), out int foundExit);
            ExecShell(shell, args, "PATH=" + emptyBin + "; " + ApkFeedConfig.BuildCheckScript(), out int missingExit);
            Check(foundExit == 0 && missingExit != 0, name + ": the apk check script tells whether apk exists");

            ExecShell(shell, args, ApkFeedConfig.BuildEnableScript(feeds, 8765, null, list, key), out int exit1);
            Check(exit1 == 0 && File.ReadAllText(list) == "# smartty-local-begin\nhttp://127.0.0.1:8765/r0/base/packages.adb\nhttp://127.0.0.1:8765/r0/lu%20ci/packages.adb\n# smartty-local-end\n" && !File.Exists(key), name + ": enable writes the marked block of repository URLs and no key");

            File.WriteAllText(list, "# my own\nhttps://example.com/packages.adb");
            File.WriteAllText(key, "stale key of an earlier run");
            ExecShell(shell, args, ApkFeedConfig.BuildEnableScript(feeds.Take(1).ToList(), 9000, TestPublicKey, list, key), out int exit2);
            Check(exit2 == 0 && File.ReadAllText(list) == "# my own\nhttps://example.com/packages.adb\n# smartty-local-begin\nhttp://127.0.0.1:9000/r0/base/packages.adb\n# smartty-local-end\n", name + ": enable keeps the user's repositories and replaces the old block");
            Check(File.ReadAllText(key) == TestPublicKey + "\n", name + ": enable with a public key writes it (multi-line PEM intact)");

            ExecShell(shell, args, ApkFeedConfig.BuildEnableScript(feeds, 9000, null, list, key), out int exit3);
            Check(exit3 == 0 && !File.Exists(key), name + ": enable without a key removes the key of an earlier run");

            ExecShell(shell, args, ApkFeedConfig.BuildEnableScript(feeds.Take(1).ToList(), 9000, TestPublicKey, list, key), out _);
            File.AppendAllText(list, "https://example.com/after/packages.adb\n");
            ExecShell(shell, args, ApkFeedConfig.BuildDisableScript(list, key), out int exit4);
            Check(exit4 == 0 && File.ReadAllText(list) == "# my own\nhttps://example.com/packages.adb\nhttps://example.com/after/packages.adb\n" && !File.Exists(key), name + ": disable removes the block and the key, nothing else");
            ExecShell(shell, args, ApkFeedConfig.BuildDisableScript(list, key), out int exit5);
            ExecShell(shell, args, ApkFeedConfig.BuildDisableScript(Path.Combine(dir, "no-such.list"), Path.Combine(dir, "no-such.pem")), out int exit6);
            Check(exit5 == 0 && exit6 == 0, name + ": disable succeeds when there is nothing to remove");

            string hostile = "x'; touch " + Path.Combine(dir, "pwned") + "; '";
            ExecShell(shell, args, ApkFeedConfig.BuildEnableScript(feeds, 1, "-----BEGIN PUBLIC KEY-----\n" + hostile + "\n-----END PUBLIC KEY-----", list, key), out _);
            Check(!File.Exists(Path.Combine(dir, "pwned")), name + ": quotes inside the key do not run commands");
        }
    }

    private static async Task ApkServerAndHostTests()
    {
        // Serving: the index and the packages of the build are served as is; a stale index is reported.
        string tree = Path.Combine(_work, "apkserver");
        string root = Path.Combine(tree, "bin", "packages", "x86_64");
        string feed = Path.Combine(root, "base");
        Directory.CreateDirectory(feed);
        byte[] index = new byte[5000];
        new Random(1).NextBytes(index);
        byte[] package = new byte[200 * 1024];
        new Random(2).NextBytes(package);
        File.WriteAllBytes(Path.Combine(feed, "packages.adb"), index);
        File.WriteAllBytes(Path.Combine(feed, "tool-1.0-r1.apk"), package);
        File.SetLastWriteTimeUtc(Path.Combine(feed, "packages.adb"), DateTime.UtcNow.AddMinutes(5));
        List<string> log = new List<string>();
        using (LocalRepoServer server = new LocalRepoServer(new[] { root }, 0))
        using (HttpClient client = new HttpClient())
        {
            server.RequestLogged += (string line) => { lock (log) { log.Add(line); } };
            server.Start();
            string baseUrl = "http://127.0.0.1:" + server.Port;
            (int status, byte[] body) adb = await GetAsync(client, baseUrl + "/r0/base/packages.adb");
            Check(adb.status == 200 && adb.body.SequenceEqual(index), "packages.adb is served byte for byte");
            (int status, byte[] body) apk = await GetAsync(client, baseUrl + "/r0/base/tool-1.0-r1.apk");
            Check(apk.status == 200 && apk.body.SequenceEqual(package), "an .apk is served byte for byte (200 KiB)");
            Check((await GetAsync(client, baseUrl + "/r0/base/Packages")).status == 404, "an apk feed has no opkg index");
            Check((await GetAsync(client, baseUrl + "/r0/base/../../../../secret")).status != 200, "path traversal is refused for apk feeds too");
            server.Feeds = LocalRepoFeedScanner.Scan(new[] { root });
            string startPage = Encoding.UTF8.GetString((await GetAsync(client, baseUrl + "/")).body);
            Check(startPage.Contains("href=\"/r0/base/packages.adb\"") && !startPage.Contains("/r0/base/Packages"), "the start page links an apk feed to its packages.adb, not to an opkg index");
            lock (log)
            {
                Check(log.Any(l => l.StartsWith("GET /r0/base/packages.adb - 200") && !l.Contains("warning")), "a fresh index is logged without a warning");
            }
            File.SetLastWriteTimeUtc(Path.Combine(feed, "tool-1.0-r1.apk"), DateTime.UtcNow.AddMinutes(30));
            await GetAsync(client, baseUrl + "/r0/base/packages.adb");
            lock (log)
            {
                Check(log.Any(l => l.StartsWith("GET /r0/base/packages.adb - 200") && l.Contains("warning: 1 .apk file is newer than packages.adb") && l.Contains("make package/index")), "a package newer than the index is reported with the way to fix it");
            }
            if (Has("wget"))
            {
                string target = Path.Combine(_work, "wget-packages.adb");
                Check(Run("wget", "-q -O " + target + " " + baseUrl + "/r0/base/packages.adb", null, out _, out _) == 0 && File.ReadAllBytes(target).SequenceEqual(index), "wget downloads packages.adb");
            }
        }

        // The host with an apk device: only apk feeds are served, the apk repository list and key are managed.
        string conf = Path.Combine(_work, "apkhost", "customfeeds.list");
        string keyFile = Path.Combine(_work, "apkhost", "keys", "smartty-local.pem");
        Directory.CreateDirectory(Path.GetDirectoryName(conf));
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile));
        File.WriteAllText(Path.Combine(tree, "public-key.pem"), TestPublicKey + "\n");
        MakeIpk(Path.Combine(root, "ipk"), "foo", "1.0", "An opkg feed in the same tree");
        File.WriteAllText(conf, "# the user's own repository\nhttps://example.com/packages.adb\n");

        SshSession NewApkSession()
        {
            SshSession s = NewFakeSession(null);
            s.LocalConfigFile = null;
            s.ApkConfigFile = conf;
            s.ApkKeyFile = keyFile;
            return s;
        }

        LocalRepoHost host = new LocalRepoHost();
        SshSession session = NewApkSession();
        host.Start(session, new[] { root }, 0, LocalRepoPackageManager.Apk);
        int port = host.Port;
        Check(host.Manager == LocalRepoPackageManager.Apk && host.Feeds.Count == 1 && Path.GetFileName(host.Feeds[0].Directory) == "base", "Start serves only the apk feeds of the tree");
        Check(File.ReadAllText(conf) == "# the user's own repository\nhttps://example.com/packages.adb\n# smartty-local-begin\nhttp://127.0.0.1:" + port + "/r0/base/packages.adb\n# smartty-local-end\n" && !File.Exists(keyFile), "Start adds the repository to the apk list and keeps the user's lines, no key by default");
        Check(host.DescribeFeed(host.Feeds[0]) == "http://127.0.0.1:" + port + "/r0/base/packages.adb", "the feed is described by its repository line");
        Check(session.Commands.Contains("command -v apk >/dev/null 2>&1") && !session.Commands.Contains("command -v opkg >/dev/null 2>&1"), "apk is checked on the device, opkg is not");
        host.Stop(session);
        Check(File.ReadAllText(conf) == "# the user's own repository\nhttps://example.com/packages.adb\n" && !host.IsRunning && !PortIsOpen(port), "Stop removes the repository again");

        host.Start(session, new[] { root }, 0, LocalRepoPackageManager.Apk, trustBuildKey: true);
        Check(File.ReadAllText(keyFile) == TestPublicKey + "\n", "with trust requested the public key of the build tree is written to the keys directory");
        Check(host.GetHistory().Any(l => l.Contains("the key of the build is trusted by apk")), "and the log says so");
        File.WriteAllText(conf, "# wiped by a reboot\n");
        File.Delete(keyFile);
        SshSession next = NewApkSession();
        host.Reattach(next);
        Check(host.IsTunnelCurrent(next) && File.ReadAllText(conf).Contains("/r0/base/packages.adb") && File.ReadAllText(keyFile) == TestPublicKey + "\n", "Reattach restores the repository and the key on the new connection");
        host.Stop(next);
        Check(!File.Exists(keyFile) && !File.ReadAllText(conf).Contains("smartty-local"), "Stop removes the key as well");
        File.WriteAllText(keyFile, "left over");
        File.WriteAllText(conf, "a\n# smartty-local-begin\nhttp://127.0.0.1:1/r0/x/packages.adb\n# smartty-local-end\nb\n");
        Check(host.RemoveFeeds(NewApkSession(), LocalRepoPackageManager.Apk) == null && File.ReadAllText(conf) == "a\nb\n" && !File.Exists(keyFile), "RemoveFeeds cleans up the leftovers of an earlier apk run");

        // Refusals.
        string opkgOnly = Path.Combine(_work, "apkhost-opkgonly");
        MakeIpk(Path.Combine(opkgOnly, "base"), "bar", "1.0", "opkg only");
        LocalRepoHost refused = new LocalRepoHost();
        SshSession apkDevice = NewApkSession();
        InvalidOperationException wrongKind = Catch<InvalidOperationException>(() => refused.Start(apkDevice, new[] { opkgOnly }, 0, LocalRepoPackageManager.Apk));
        Check(wrongKind != null && wrongKind.Message.Contains("only opkg (.ipk) feeds") && wrongKind.Message.Contains("the device uses apk") && apkDevice.TunnelStarts == 0, "opkg feeds for an apk device: refused with an explanation");
        InvalidOperationException otherWay = Catch<InvalidOperationException>(() => refused.Start(NewFakeSession(conf), new[] { root.Replace("x86_64", "x86_64") + "/base" }, 0, LocalRepoPackageManager.Opkg));
        Check(otherWay != null && otherWay.Message.Contains("only apk (.apk) feeds") && otherWay.Message.Contains("the device uses opkg"), "apk feeds for an opkg device: refused with an explanation");
        SshSession noApk = NewApkSession();
        noApk.ApkMissing = true;
        Check(Throws<InvalidOperationException>(() => refused.Start(noApk, new[] { root }, 0, LocalRepoPackageManager.Apk)) && noApk.TunnelStarts == 0 && !File.ReadAllText(conf).Contains("smartty"), "no apk on the device: refused before anything is changed");
        string keyless = Path.Combine(_work, "apkhost-nokey", "bin", "packages", "x86_64");
        Directory.CreateDirectory(Path.Combine(keyless, "base"));
        File.WriteAllText(Path.Combine(keyless, "base", "packages.adb"), "adb");
        SshSession keylessSession = NewApkSession();
        InvalidOperationException noKey = Catch<InvalidOperationException>(() => refused.Start(keylessSession, new[] { keyless }, 0, LocalRepoPackageManager.Apk, trustBuildKey: true));
        Check(noKey != null && noKey.Message.Contains("public-key.pem") && keylessSession.TunnelStarts == 0 && !refused.IsRunning && !File.ReadAllText(conf).Contains("smartty"), "trust requested but no public key above the folders: refused, nothing changed");
        SshSession noTunnel = NewApkSession();
        noTunnel.FailTunnel = true;
        Check(Throws<InvalidOperationException>(() => refused.Start(noTunnel, new[] { root }, 0, LocalRepoPackageManager.Apk)) && !File.ReadAllText(conf).Contains("smartty"), "the tunnel is refused: nothing configured");
        SshSession noConfig = NewApkSession();
        noConfig.FailEnable = true;
        Check(Throws<InvalidOperationException>(() => refused.Start(noConfig, new[] { root }, 0, LocalRepoPackageManager.Apk)) && noConfig.TunnelStops == 1 && !refused.IsRunning, "the repository list cannot be written: the tunnel is closed again");
    }

    /// <summary>
    /// The whole chain with the real apk-tools 3: the host writes the repository list and the key, the HTTP server serves the signed
    /// index and packages, and apk resolves a dependency, installs, and refuses an untrusted repository. Skipped when apk-tools 3 or
    /// openssl is not installed (set SMARTTY_APK to the path of an apk binary).
    /// </summary>
    private static async Task ApkEndToEndTests()
    {
        string apk = FindApk();
        if (apk == null || !Has("openssl"))
        {
            Skip("end-to-end with the real apk", "apk-tools 3 or openssl is not installed; set SMARTTY_APK to an apk binary");
            return;
        }
        string tree = Path.Combine(_work, "e2e");
        string root = Path.Combine(tree, "bin", "packages", "x86_64");
        string feed = Path.Combine(root, "base");
        Directory.CreateDirectory(feed);
        string priv = Path.Combine(tree, "private-key.pem");
        string pub = Path.Combine(tree, "public-key.pem");
        Check(Run("openssl", "genrsa -out " + priv + " 2048", null, out _, out _) == 0 && Run("openssl", "rsa -in " + priv + " -pubout -out " + pub, null, out _, out _) == 0, "e2e: a signing key pair is created");

        void MakePackage(string name, string version, string depends)
        {
            string files = Path.Combine(_work, "e2e-files-" + name);
            Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(files, name + ".txt"), name + "\n");
            List<string> arguments = new List<string> { "mkpkg", "--info", "name:" + name, "--info", "version:" + version, "--info", "arch:x86_64", "--info", "description:Test " + name };
            if (depends != null)
            {
                arguments.AddRange(new[] { "--info", "depends:" + depends });
            }
            arguments.AddRange(new[] { "--files", files, "--sign", priv, "--output", Path.Combine(feed, name + "-" + version + ".apk") });
            Check(RunArgs(apk, arguments.ToArray(), out _, out string error) == 0, "e2e: mkpkg " + name + " " + error);
        }

        void MakeIndex()
        {
            ProcessStartInfo info = new ProcessStartInfo(apk) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = feed };
            foreach (string argument in new[] { "mkndx", "--allow-untrusted", "--sign", priv, "--output", "packages.adb" }.Concat(Directory.GetFiles(feed, "*.apk").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)))
            {
                info.ArgumentList.Add(argument);
            }
            using (Process process = Process.Start(info))
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                Check(process.ExitCode == 0, "e2e: mkndx");
            }
        }

        MakePackage("hello", "1.0-r1", null);
        MakePackage("hello-dep", "2.0-r1", "hello");
        MakeIndex();

        string device = Path.Combine(_work, "e2e-device");
        string list = Path.Combine(device, "repositories.d", "customfeeds.list");
        string deviceKeys = Path.Combine(device, "keys");
        string keyFile = Path.Combine(deviceKeys, "smartty-local.pem");
        Directory.CreateDirectory(Path.GetDirectoryName(list));
        Directory.CreateDirectory(deviceKeys);
        SshSession session = NewFakeSession(null);
        session.LocalConfigFile = null;
        session.ApkConfigFile = list;
        session.ApkKeyFile = keyFile;

        string[] Apk(string rootDir, string keysDir, params string[] command)
        {
            return new[] { "--root", rootDir, "--arch", "x86_64", "--keys-dir", keysDir, "--repositories-file", list, "--no-cache" }.Concat(command).ToArray();
        }

        string NewDeviceRoot(string name)
        {
            string dir = Path.Combine(_work, "e2e-root-" + name);
            Directory.CreateDirectory(Path.Combine(dir, "etc", "apk"));
            Directory.CreateDirectory(Path.Combine(dir, "lib", "apk", "db"));
            return dir;
        }

        // The key is not trusted: apk ignores the repository and cannot find the package.
        LocalRepoHost untrusted = new LocalRepoHost();
        untrusted.Start(session, new[] { root }, 0, LocalRepoPackageManager.Apk);
        string untrustedRoot = NewDeviceRoot("untrusted");
        int untrustedExit = RunArgs(apk, Apk(untrustedRoot, deviceKeys, "add", "--initdb", "--no-scripts", "hello"), out string untrustedOut, out string untrustedErr);
        Check(untrustedExit != 0 && !File.Exists(Path.Combine(untrustedRoot, "hello.txt")), "e2e: a device that does not trust the key of the build does not take its packages");
        untrusted.Stop(session);

        // With the key trusted by SmarTTY the same repository works: index, dependency and packages come over HTTP.
        List<string> log = new List<string>();
        LocalRepoHost host = new LocalRepoHost();
        host.Log += (string line) => { lock (log) { log.Add(line); } };
        host.Start(session, new[] { root }, 0, LocalRepoPackageManager.Apk, trustBuildKey: true);
        string deviceRoot = NewDeviceRoot("trusted");
        int addExit = RunArgs(apk, Apk(deviceRoot, deviceKeys, "add", "--initdb", "--no-scripts", "hello-dep"), out string addOut, out string addErr);
        Check(addExit == 0 && File.Exists(Path.Combine(deviceRoot, "hello.txt")) && File.Exists(Path.Combine(deviceRoot, "hello-dep.txt")), "e2e: apk installs a package and its dependency from the served repository " + addOut + addErr);
        int listExit = RunArgs(apk, Apk(deviceRoot, deviceKeys, "list", "--installed"), out string installed, out _);
        Check(listExit == 0 && installed.Contains("hello-1.0-r1") && installed.Contains("hello-dep-2.0-r1"), "e2e: both packages are installed");
        lock (log)
        {
            Check(log.Any(l => l.Contains("GET /r0/base/packages.adb - 200")) && log.Any(l => l.Contains("GET /r0/base/hello-dep-2.0-r1.apk - 200")) && log.Any(l => l.Contains("GET /r0/base/hello-1.0-r1.apk - 200")), "e2e: the log shows the index and both packages fetched by apk");
        }

        // A package built later is not in the index until the index is made again; the log says so.
        MakePackage("late", "3.0-r1", null);
        File.SetLastWriteTimeUtc(Path.Combine(feed, "late-3.0-r1.apk"), File.GetLastWriteTimeUtc(Path.Combine(feed, "packages.adb")).AddMinutes(10));
        RunArgs(apk, Apk(deviceRoot, deviceKeys, "update"), out _, out _);
        int missingExit = RunArgs(apk, Apk(deviceRoot, deviceKeys, "add", "--no-scripts", "late"), out _, out _);
        Check(missingExit != 0, "e2e: a package that is not in the index cannot be installed");
        lock (log)
        {
            Check(log.Any(l => l.Contains("GET /r0/base/packages.adb - 200") && l.Contains("warning: 1 .apk file is newer than packages.adb")), "e2e: the log warns about the package that is not in the index");
        }
        MakeIndex();
        RunArgs(apk, Apk(deviceRoot, deviceKeys, "update"), out _, out _);
        int lateExit = RunArgs(apk, Apk(deviceRoot, deviceKeys, "add", "--no-scripts", "late"), out _, out string lateErr);
        Check(lateExit == 0 && File.Exists(Path.Combine(deviceRoot, "late.txt")), "e2e: after the index is made again the package installs " + lateErr);

        host.Stop(session);
        Check(!File.Exists(keyFile) && !File.ReadAllText(list).Contains("smartty-local"), "e2e: Stop takes the repository and the key away");
        int afterStop = RunArgs(apk, Apk(NewDeviceRoot("after"), deviceKeys, "add", "--initdb", "--no-scripts", "hello"), out _, out _);
        Check(afterStop != 0, "e2e: after Stop the device no longer sees the packages");
        await Task.CompletedTask;
    }

    private static async Task<int> Main()
    {
        _work = Path.Combine(Path.GetTempPath(), "smartty-localrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        try
        {
            if (!Has("tar"))
            {
                Console.WriteLine("SKIP: the system tar is missing, nothing to check");
                return 0;
            }
            IndexTests();
            StaleTests();
            ScannerTests();
            ResolveTests();
            DetectorTests();
            await ServerTests();
            ConfigTests();
            OpkgSignatureTests();
            ArIpkTests();
            await HostTests();
            SignatureHostTests();
            await TargetPackagesTests();
            TargetPackageFolderTests();
            FolderPathTests();
            SigningScriptTests();
            SigningShellTests();
            await SignedIndexServerTests();
            await SigningHostTests();
            ApkDetectorAndScannerTests();
            ApkConfigTests();
            await ApkServerAndHostTests();
            await ApkEndToEndTests();
            Console.WriteLine();
            Console.WriteLine("All checks passed: " + _passed + " passed, " + _skipped + " skipped");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            Console.WriteLine(exception.StackTrace);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(_work, true);
            }
            catch
            {
            }
        }
    }
}
