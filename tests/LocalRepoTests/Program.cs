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
        string onlyIndex = Path.Combine(_work, "onlyindex");
        Directory.CreateDirectory(onlyIndex);
        File.WriteAllText(Path.Combine(onlyIndex, "Packages"), "x");
        Check(!IpkIndexBuilder.NeedsGeneratedIndex(onlyIndex), "an index without .ipk files is never regenerated");
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
            await ServerTests();
            ConfigTests();
            await HostTests();
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
