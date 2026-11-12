using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SmarTTY.Transport;

// Regression harness for Tools > Flash (MTD) backup and restore (Transport/MtdFlash.cs, Transport/MtdFlashJobs.cs): the reading of
// /proc/mtd and sysfs, the layout of the whole flash, the description file of a backup, the choice of the RAM file system, and the
// backup and restore jobs run for real by /bin/sh and by BusyBox sh. The "flash partitions" are files and the flash tools (mtd,
// flashcp, flash_erase + nandwrite) are small scripts that write into them, so every step of a restore is checked: the image here,
// the image in RAM, the write and the read-back, and that a failure in any of them leaves the partition alone or is reported.
// Run with: dotnet run --project tests/MtdFlashTests
internal static class Program
{
    private static int _passed;

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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private const string OpenWrtProcMtd =
        "dev:    size   erasesize  name\n" +
        "mtd0: 00030000 00010000 \"u-boot\"\n" +
        "mtd1: 00010000 00010000 \"u-boot-env\"\n" +
        "mtd2: 00010000 00010000 \"factory\"\n" +
        "mtd3: 00fb0000 00010000 \"firmware\"\n" +
        "mtd4: 0023cc5f 00010000 \"kernel\"\n" +
        "mtd5: 00d733a1 00010000 \"rootfs\"\n" +
        "mtd6: 00a40000 00010000 \"rootfs_data\"\n";

    private static int Main()
    {
        _work = Path.Combine(Path.GetTempPath(), "smartty-mtd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        try
        {
            ParseTests();
            LayoutTests();
            ManifestTests();
            RamTests();
            RuleTests();
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("SKIP: the shell commands are not run on Windows");
            }
            else
            {
                string busybox = new[] { "/usr/bin/busybox", "/bin/busybox" }.FirstOrDefault(File.Exists);
                List<(string Label, string Shell)> shells = new List<(string, string)> { ("sh", null) };
                if (busybox != null)
                {
                    shells.Add(("BusyBox", busybox));
                }
                else
                {
                    Console.WriteLine("SKIP: busybox is not installed, the commands are not run with the tools of OpenWrt");
                }
                foreach ((string label, string bb) in shells)
                {
                    InfoCommandTests(label, bb);
                    CommandTests(label, bb);
                    JobTests(label, bb);
                }
            }
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
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

    private static void ParseTests()
    {
        Equal(MtdFlash.FromProbe(new[] { "SMARTTY_TAR=yes", "SMARTTY_MTD=yes" }), (bool?)true, "probe: flash found");
        Equal(MtdFlash.FromProbe(new[] { "SMARTTY_MTD=no" }), (bool?)false, "probe: no flash");
        Equal(MtdFlash.FromProbe(new[] { "garbage" }), (bool?)null, "probe: no answer");

        MtdPartition firmware = MtdFlash.ParseProcMtdLine("mtd3: 00fb0000 00010000 \"firmware\"");
        Check(firmware.Index == 3 && firmware.Size == 0xfb0000 && firmware.EraseSize == 0x10000 && firmware.Name == "firmware", "/proc/mtd: a line");
        Check(MtdFlash.ParseProcMtdLine("mtd12: 00100000 00020000 \"my name\"").Name == "my name" && MtdFlash.ParseProcMtdLine("mtd12: 00100000 00020000 \"my name\"").Index == 12, "/proc/mtd: a name with a space, two digits");
        Check(MtdFlash.ParseProcMtdLine("dev:    size   erasesize  name") == null && MtdFlash.ParseProcMtdLine("mtdx: 1 2 \"a\"") == null, "/proc/mtd: the header and garbage are skipped");

        MtdHostInfo info = MtdFlash.ParseInfo(new[]
        {
            "SMARTTY_UID=0",
            "SMARTTY_MTD mtd0: 00030000 00010000 \"u-boot\"",
            "SMARTTY_DEV mtd0 /dev/mtd0",
            "SMARTTY_SYS mtd0 offset=0 type=nor flags=0x800",
            "SMARTTY_MTD mtd3: 00fb0000 00010000 \"firmware\"",
            "SMARTTY_DEV mtd3 /dev/mtd3",
            "SMARTTY_SYS mtd3 offset=327680 type=nor flags=0xc00",
            "SMARTTY_MTD mtd7: 08000000 00020000 \"ubi\"",
            "SMARTTY_DEV mtd7 /dev/mtdblock7",
            "SMARTTY_SYS mtd7 offset= type=nand flags=",
            "SMARTTY_TOOL mtd",
            "SMARTTY_TOOL md5sum",
            "SMARTTY_HEADC=yes",
            "SMARTTY_MEM MemTotal 60000",
            "SMARTTY_MEM MemAvailable 30000",
            "SMARTTY_MEM MemFree 20000",
            "SMARTTY_RAM tmpfs 29000 /tmp",
            "SMARTTY_RAM tmpfs - /dev",
            "SMARTTY_RAM ramfs 0 /ram disk",
            "SMARTTY_INUSE mtd7 attached to UBI (ubi0)",
            "SMARTTY_INUSE mtd3 mounted on /rom (squashfs)"
        });
        Check(info.IsRoot == true && info.Partitions.Count == 3, "info: root and three partitions");
        MtdPartition uboot = info.Partitions[0];
        Check(uboot.Offset == 0 && uboot.Type == "nor" && uboot.Writable == false && uboot.HasCharDevice, "info: u-boot is read-only (no MTD_WRITEABLE)");
        Check(info.Partitions[1].Offset == 327680 && info.Partitions[1].Writable == true && info.Partitions[1].InUse == "mounted on /rom (squashfs)", "info: firmware is writable and in use");
        MtdPartition ubi = info.Partitions[2];
        Check(ubi.Offset == null && ubi.Flags == null && ubi.Writable == null && ubi.IsNand && !ubi.HasCharDevice && ubi.Device == "/dev/mtdblock7", "info: empty sysfs values, a block device only");
        Check(info.Tools.SetEquals(new[] { "mtd", "md5sum" }) && info.HashAlgorithm == "md5" && info.HeadHasBytes, "info: tools, md5 only, head -c");
        Check(info.MemAvailableBytes == 30000L * 1024 && info.UsableMemoryBytes == 30000L * 1024, "info: MemAvailable");
        Check(info.RamDirectories.Count == 3 && info.RamDirectories[0].FreeBytes == 29000L * 1024 && info.RamDirectories[1].FreeBytes == null && info.RamDirectories[2].Path == "/ram disk" && info.RamDirectories[2].FreeBytes == null, "info: the RAM file systems");
        Equal(MtdFlash.ParseInfo(new[] { "SMARTTY_MEM MemFree 1000", "SMARTTY_MEM Cached 500" }).UsableMemoryBytes, (long?)1500 * 1024, "info: an old kernel without MemAvailable");
        Equal(MtdFlash.ParseInfo(new[] { "SMARTTY_TOOL sha256sum", "SMARTTY_TOOL md5sum" }).HashAlgorithm, "sha256", "info: sha256 wins");
        Equal(MtdFlash.ParseInfo(new string[0]).HashAlgorithm, (string)null, "info: no checksum tool");

        Equal(MtdFlash.ParseHash(new[] { "D41D8CD98F00B204E9800998ECF8427E  -" }, "md5"), "d41d8cd98f00b204e9800998ecf8427e", "hash: md5sum output");
        Equal(MtdFlash.ParseHash(new[] { "md5sum: /x: No such file or directory" }, "md5"), (string)null, "hash: an error");
        Equal(MtdFlash.ParseHash(new[] { "d41d8cd98f00b204e9800998ecf8427e  -" }, "sha256"), (string)null, "hash: the length of the algorithm");
    }

    private static List<MtdPartition> OpenWrtPartitions(bool withOffsets)
    {
        List<MtdPartition> partitions = OpenWrtProcMtd.Split('\n').Select(MtdFlash.ParseProcMtdLine).Where(p => p != null).ToList();
        if (withOffsets)
        {
            long[] offsets = { 0, 0x30000, 0x40000, 0x50000, 0x50000, 0x28cc5f, 0x5c0000 };
            for (int i = 0; i < partitions.Count; i++)
            {
                partitions[i].Offset = offsets[i];
                partitions[i].Type = "nor";
            }
        }
        return partitions;
    }

    private static void LayoutTests()
    {
        List<MtdPartition> layout = MtdFlash.FullFlashLayout(OpenWrtPartitions(true), out string reason);
        Check(layout != null && layout.Select(p => p.Name).SequenceEqual(new[] { "u-boot", "u-boot-env", "factory", "firmware" }), "layout: the top-level partitions of OpenWrt (" + reason + ")");
        Equal(layout.Sum(p => p.Size), 0x1000000L, "layout: they make 16 MiB");
        Check(MtdFlash.FullFlashLayout(OpenWrtPartitions(false), out reason) == null && reason.Contains("offset"), "layout: no offsets, no full flash");

        List<MtdPartition> master = OpenWrtPartitions(true);
        master.Insert(0, new MtdPartition { Index = 9, Name = "spi0.0", Size = 0x1000000, Offset = 0, Type = "nor" });
        layout = MtdFlash.FullFlashLayout(master, out _);
        Check(layout != null && layout.Count == 1 && layout[0].Name == "spi0.0", "layout: a partition of the whole chip is the image");

        List<MtdPartition> gap = OpenWrtPartitions(true);
        gap.RemoveAt(1);
        Check(MtdFlash.FullFlashLayout(gap, out reason) == null && reason.Contains("0x30000-0x40000"), "layout: a gap (" + reason + ")");

        List<MtdPartition> overlap = OpenWrtPartitions(true);
        overlap[1].Size = 0x18000;
        Check(MtdFlash.FullFlashLayout(overlap, out reason) == null && reason.Contains("overlaps"), "layout: an overlap (" + reason + ")");

        List<MtdPartition> twoChips = OpenWrtPartitions(true).Take(4).ToList();
        twoChips.Add(new MtdPartition { Index = 10, Name = "nand", Size = 0x8000000, Offset = 0x1000000, Type = "nand" });
        Check(MtdFlash.FullFlashLayout(twoChips, out reason) == null && reason.Contains("different types"), "layout: NOR and NAND are not one image");
    }

    private static void ManifestTests()
    {
        MtdManifest manifest = new MtdManifest { Host = "router\tlan", Created = new DateTime(2026, 11, 2, 10, 30, 0, DateTimeKind.Utc), Kind = "fullflash" };
        manifest.Parts.Add(new MtdImagePart { File = "r-full.bin", FileOffset = 0, Size = 0x30000, MtdIndex = 0, Name = "u-boot", FlashOffset = 0, EraseSize = 0x10000, Type = "nor", Sha256 = new string('a', 64), Md5 = new string('b', 32) });
        manifest.Parts.Add(new MtdImagePart { File = "r-full.bin", FileOffset = 0x30000, Size = 0x10000, MtdIndex = 1, Name = "u-boot env", EraseSize = 0x10000 });
        string text = manifest.Write();
        Check(text.StartsWith(MtdManifest.Signature + "\n"), "manifest: the signature line");
        MtdManifest read = MtdManifest.Parse(text.Replace("\n", "\r\n"));
        Check(read.Host == "router lan" && read.Kind == "fullflash" && read.Created == manifest.Created && read.Parts.Count == 2, "manifest: read back (CR LF too)");
        MtdImagePart first = read.Parts[0];
        Check(first.File == "r-full.bin" && first.Size == 0x30000 && first.MtdIndex == 0 && first.Name == "u-boot" && first.FlashOffset == 0 && first.Type == "nor" && first.Sha256 == new string('a', 64) && first.Md5 == new string('b', 32), "manifest: every field of a part");
        Check(read.Parts[1].FileOffset == 0x30000 && read.Parts[1].Name == "u-boot env" && read.Parts[1].FlashOffset == null && read.Parts[1].Sha256 == null, "manifest: a name with a space, missing fields");
        bool threw = false;
        try
        {
            MtdManifest.Parse("hello\n");
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        Check(threw, "manifest: another file is refused");
        threw = false;
        try
        {
            MtdManifest.Parse(MtdManifest.Signature + "\npart\tname=x\n");
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        Check(threw, "manifest: a part without file and size is refused");
    }

    private static void RamTests()
    {
        const long MiB = 1024 * 1024;
        MtdHostInfo info = new MtdHostInfo { MemAvailableBytes = 40 * MiB };
        info.RamDirectories.Add(new MtdRamDirectory { Path = "/dev", Type = "tmpfs", FreeBytes = 100 * MiB });
        info.RamDirectories.Add(new MtdRamDirectory { Path = "/run", Type = "tmpfs", FreeBytes = 5 * MiB });
        info.RamDirectories.Add(new MtdRamDirectory { Path = "/tmp", Type = "tmpfs", FreeBytes = 30 * MiB });
        Equal(MtdFlash.ChooseRamDirectory(info, 16 * MiB, out string reason)?.Path, "/tmp", "RAM: /tmp first, never /dev");
        Check(MtdFlash.ChooseRamDirectory(info, 33 * MiB, out reason) == null && reason.Contains("memory"), "RAM: the image and the reserve must fit in the memory (" + reason + ")");
        info.MemAvailableBytes = 400 * MiB;
        Check(MtdFlash.ChooseRamDirectory(info, 31 * MiB, out reason) == null && reason.Contains("room"), "RAM: the image must fit in the file system (" + reason + ")");
        info.RamDirectories.Add(new MtdRamDirectory { Path = "/mnt/ram", Type = "ramfs" });
        Equal(MtdFlash.ChooseRamDirectory(info, 31 * MiB, out _)?.Path, "/mnt/ram", "RAM: ramfs has no limit but the memory");
        Check(MtdFlash.ChooseRamDirectory(new MtdHostInfo { MemAvailableBytes = 400 * MiB }, MiB, out reason) == null && reason.Contains("no file system in RAM"), "RAM: none at all");
        Check(MtdFlash.ChooseRamDirectory(new MtdHostInfo(), MiB, out reason) == null && reason.Contains("meminfo"), "RAM: the memory is unknown");
        Check(MtdFlash.MemoryMargin(MiB) == 8 * MiB && MtdFlash.MemoryMargin(128 * MiB) == 32 * MiB, "RAM: the reserve");
        Equal(MtdFlash.RamImagePath(new MtdRamDirectory { Path = "/tmp/" }, "abc"), "/tmp/smartty-mtd-abc.bin", "RAM: the image path");
    }

    private static void RuleTests()
    {
        Check(MtdFlash.RiskNote(new MtdPartition { Name = "u-boot" }).Contains("boot loader") && MtdFlash.RiskNote(new MtdPartition { Name = "u-boot-env" }).Contains("settings"), "risk: boot loader and its settings");
        Check(MtdFlash.RiskNote(new MtdPartition { Name = "art" }).Contains("calibration") && MtdFlash.RiskNote(new MtdPartition { Name = "Factory" }).Contains("calibration"), "risk: calibration data");
        Check(MtdFlash.RiskNote(new MtdPartition { Name = "data", InUse = "mounted on /overlay (jffs2)" }).Contains("in use"), "risk: in use");
        Check(MtdFlash.RiskNote(new MtdPartition { Name = "firmware" }).Contains("running system") && MtdFlash.RiskNote(new MtdPartition { Name = "spare" }) == null, "risk: the firmware, and none");

        MtdHostInfo info = new MtdHostInfo();
        info.Tools.Add("md5sum");
        MtdPartition nor = new MtdPartition { Index = 3, Name = "firmware", Size = 0x1000, Type = "nor", Device = "/dev/mtd3", Flags = 0xc00 };
        MtdPartition nand = new MtdPartition { Index = 4, Name = "ubi", Size = 0x1000, Type = "nand", Device = "/dev/mtd4" };
        Check(MtdFlash.BuildWriteCommand(info, nor, "/tmp/i", false, out _, out string reason) == null && reason.Contains("flashcp"), "write: no tool for NOR");
        info.Tools.Add("flashcp");
        Check(Plain(MtdFlash.BuildWriteCommand(info, nor, "/tmp/i", false, out string tool, out _)).Contains("flashcp '/tmp/i' '/dev/mtd3'") && tool == "flashcp", "write: flashcp for NOR");
        Check(MtdFlash.BuildWriteCommand(info, nand, "/tmp/i", false, out _, out reason) == null && reason.Contains("nandwrite"), "write: flashcp is not for NAND");
        info.Tools.Add("flash_erase");
        info.Tools.Add("nandwrite");
        Check(MtdFlash.BuildWriteCommand(info, nand, "/tmp/i", true, out tool, out _).StartsWith("sudo -n ") && tool == "flash_erase + nandwrite", "write: flash_erase and nandwrite for NAND, with sudo");
        // NAND: erase to the end of the partition (0 0), never a block count computed from the image: a bad block that
        // flash_erase counts but nandwrite skips would push the end of the image onto blocks that were not erased.
        string nandCommand = Plain(MtdFlash.BuildWriteCommand(info, nand, "/tmp/i", false, out _, out _));
        Check(nandCommand.Contains("flash_erase '/dev/mtd4' 0 0 && nandwrite -p '/dev/mtd4' '/tmp/i'") && !nandCommand.Contains("$(("), "write: NAND erases to the end of the partition");
        MtdPartition nandWithErase = new MtdPartition { Index = 4, Name = "ubi", Size = 0x1000, EraseSize = 0x200, Type = "nand", Device = "/dev/mtd4" };
        string nandCommand2 = Plain(MtdFlash.BuildWriteCommand(info, nandWithErase, "/tmp/i", false, out _, out _));
        Check(nandCommand2.Contains("flash_erase '/dev/mtd4' 0 0 &&") && !nandCommand2.Contains("$(("), "write: NAND with a known erase size still erases to the end");
        info.Tools.Add("mtd");
        Check(Plain(MtdFlash.BuildWriteCommand(info, nand, "/tmp/i", false, out tool, out _)).Contains("mtd write '/tmp/i' '/dev/mtd4'") && tool == "mtd", "write: mtd of OpenWrt first");
        Check(MtdFlash.BuildWriteCommand(info, new MtdPartition { Index = 5, Name = "b", Device = "/dev/mtdblock5" }, "/tmp/i", false, out _, out reason) == null && reason.Contains("character device"), "write: a block device is not written");
        Check(MtdFlash.BuildWriteCommand(info, new MtdPartition { Index = 6, Name = "v", Device = "/dev/mtd6", Type = "ubi" }, "/tmp/i", false, out _, out reason) == null && reason.Contains("ubi"), "write: a UBI volume is not raw flash");

        List<string> errors = new List<string>();
        List<string> warnings = new List<string>();
        MtdFlash.CheckRestore(new MtdImagePart { Size = 0x800, Name = "firmware" }, nor, info, errors, warnings);
        Check(errors.Count == 0 && warnings.Any(w => w.Contains("smaller")) && warnings.Any(w => w.Contains("another size")) && warnings.Any(w => w.Contains("running system")), "check: a smaller image is asked about (" + string.Join("; ", warnings) + ")");
        errors.Clear();
        warnings.Clear();
        MtdFlash.CheckRestore(new MtdImagePart { Size = 0x2000, Name = "kernel" }, new MtdPartition { Index = 0, Name = "u-boot", Size = 0x1000, Type = "nor", Device = "/dev/mtd0", Flags = 0x800 }, info, errors, warnings);
        Check(errors.Any(e => e.Contains("bigger")) && errors.Any(e => e.Contains("read-only")) && warnings.Any(w => w.Contains("\"kernel\"")) && warnings.Any(w => w.Contains("boot loader")), "check: too big and read-only stop it (" + string.Join("; ", errors) + ")");
        errors.Clear();
        MtdFlash.CheckRestore(new MtdImagePart { Size = 1 }, null, info, errors, warnings);
        Check(errors.Count == 1 && errors[0].Contains("no partition"), "check: no target");
        errors.Clear();
        MtdFlash.CheckRestore(new MtdImagePart { Size = 1 }, nor, new MtdHostInfo(), errors, warnings);
        Check(errors.Any(e => e.Contains("sha256sum")) && errors.Any(e => e.Contains("flashcp")), "check: no checksum and no write tool");
        // A smaller image on NAND written with flash_erase + nandwrite: the rest of the partition is erased, and the warning says so.
        MtdHostInfo nandTools = new MtdHostInfo();
        nandTools.Tools.Add("md5sum");
        nandTools.Tools.Add("flash_erase");
        nandTools.Tools.Add("nandwrite");
        errors.Clear();
        warnings.Clear();
        MtdFlash.CheckRestore(new MtdImagePart { Size = 0x800, Name = "ubi" }, new MtdPartition { Index = 4, Name = "ubi", Size = 0x1000, Type = "nand", Device = "/dev/mtd4" }, nandTools, errors, warnings);
        Check(errors.Count == 0 && warnings.Any(w => w.Contains("smaller") && w.Contains("erased") && w.Contains("0xFF")), "check: a smaller image on NAND warns that the rest is erased (" + string.Join("; ", warnings) + ")");
        warnings.Clear();
        MtdFlash.CheckRestore(new MtdImagePart { Size = 0x800, Name = "firmware" }, nor, info, errors, warnings);
        Check(!warnings.Any(w => w.Contains("0xFF")), "check: a smaller image with mtd or flashcp does not claim the rest is erased");
        errors.Clear();
        warnings.Clear();

        List<MtdPartition> partitions = OpenWrtPartitions(true);
        Equal(MtdFlash.MatchPartition(new MtdImagePart { Name = "firmware", Size = 0xfb0000 }, partitions)?.Index, 3, "match: by name and size");
        Equal(MtdFlash.MatchPartition(new MtdImagePart { Name = "firmware", Size = 1 }, partitions)?.Index, 3, "match: by name");
        Check(MtdFlash.MatchPartition(new MtdImagePart { Name = "other" }, partitions) == null && MtdFlash.MatchPartition(new MtdImagePart(), partitions) == null, "match: none");
        Equal(MtdFlash.ImageFileName(MtdFlash.FilePrefix("router.lan", new DateTime(2026, 11, 2, 10, 30, 0)), partitions[1]), "router.lan-20261102-1030-mtd1-u-boot-env.bin", "file name");
        Equal(MtdFlash.FilePrefix("::1", DateTime.MinValue).StartsWith("1-"), true, "file name: no character Windows forbids");
    }

    /// <summary>The info script run against a made-up system: /proc, /sys and /dev are folders of the test.</summary>
    private static void InfoCommandTests(string label, string busybox)
    {
        string root = Path.Combine(_work, "sys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "proc"));
        Directory.CreateDirectory(Path.Combine(root, "dev"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        File.WriteAllText(Path.Combine(root, "proc", "mtd"), OpenWrtProcMtd);
        File.WriteAllText(Path.Combine(root, "proc", "meminfo"), "MemTotal:          60000 kB\nMemFree:           20000 kB\nMemAvailable:      30000 kB\nBuffers:            1000 kB\nCached:             9000 kB\n");
        File.WriteAllText(Path.Combine(root, "proc", "mounts"), "/dev/root /rom squashfs ro 0 0\ntmpfs " + Path.Combine(root, "tmp") + " tmpfs rw 0 0\n/dev/mtdblock6 /overlay jffs2 rw 0 0\ntmpfs /no\\040such tmpfs rw 0 0\n");
        long[] offsets = { 0, 0x30000, 0x40000, 0x50000, 0x50000, 0x28cc5f, 0x5c0000 };
        for (int i = 0; i < 7; i++)
        {
            string sys = Path.Combine(root, "sys", "class", "mtd", "mtd" + i);
            Directory.CreateDirectory(sys);
            File.WriteAllText(Path.Combine(sys, "offset"), offsets[i] + "\n");
            File.WriteAllText(Path.Combine(sys, "type"), "nor\n");
            File.WriteAllText(Path.Combine(sys, "flags"), (i == 0 ? "0x800" : "0xc00") + "\n");
            if (i != 5)
            {
                File.WriteAllText(Path.Combine(root, "dev", "mtd" + i), "");
            }
        }
        Directory.CreateDirectory(Path.Combine(root, "sys", "class", "mtd", "mtd0ro"));
        Directory.CreateDirectory(Path.Combine(root, "sys", "class", "ubi", "ubi0"));
        File.WriteAllText(Path.Combine(root, "sys", "class", "ubi", "ubi0", "mtd_num"), "4\n");
        string script = MtdFlash.InfoCommand.Replace("/proc/", root + "/proc/").Replace("/sys/", root + "/sys/").Replace("/dev/$n", root + "/dev/$n").Replace("[ -c ", "[ -f ");
        int exit = Run(busybox, SmarTTY.Utilities.PosixShell.Wrap(script), out byte[] output, out string error);
        MtdHostInfo info = MtdFlash.ParseInfo(Lines(output));
        Check(exit == 0 && info.Partitions.Count == 7, label + " info: the seven partitions of /proc/mtd (" + error.Trim() + ")");
        Check(info.Partitions[3].Name == "firmware" && info.Partitions[3].Size == 0xfb0000 && info.Partitions[3].Offset == 0x50000 && info.Partitions[3].Type == "nor" && info.Partitions[3].Writable == true, label + " info: firmware with its sysfs values");
        Check(info.Partitions[0].Writable == false && info.Partitions[0].Device == root + "/dev/mtd0" && info.Partitions[5].Device == null, label + " info: read-only u-boot, the device nodes");
        Check(info.Partitions[6].InUse == "mounted on /overlay (jffs2)" && info.Partitions[4].InUse == "attached to UBI (ubi0)", label + " info: the partitions in use");
        Check(info.MemAvailableBytes == 30000L * 1024 && info.CachedBytes == 9000L * 1024, label + " info: the memory");
        Check(info.RamDirectories.Count == 1 && info.RamDirectories[0].Path == Path.Combine(root, "tmp") && info.RamDirectories[0].FreeBytes > 0, label + " info: the RAM file system with its free space, a path with an escape left out");
        Check(info.Tools.Contains("md5sum") && info.HeadHasBytes, label + " info: the tools and head -c");
        Check(MtdFlash.FullFlashLayout(info.Partitions, out _)?.Count == 4, label + " info: the full flash layout from sysfs");
    }

    /// <summary>The commands of the jobs on files that stand for the partitions.</summary>
    private static void CommandTests(string label, string busybox)
    {
        byte[] content = RandomBytes(3 * 65536 + 512, 1);
        string device = Path.Combine(_work, "mtd-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(device, content);
        MtdPartition partition = new MtdPartition { Index = 3, Name = "firmware", Size = content.Length, Device = device, Type = "nor" };

        int exit = Run(busybox, MtdFlash.BuildReadCommand(partition, false), out byte[] read, out _);
        Check(exit == 0 && read.SequenceEqual(content), label + ": cat reads the whole partition to stdout");
        foreach (bool head in new[] { true, false })
        {
            foreach (long size in new long[] { content.Length, 65536, 1000 })
            {
                foreach (string algorithm in new[] { "sha256", "md5" })
                {
                    Run(busybox, MtdFlash.BuildDeviceHashCommand(device, size, algorithm, head, false), out byte[] hashOutput, out string hashError);
                    Equal(MtdFlash.ParseHash(Lines(hashOutput), algorithm), Hash(content.Take((int)size).ToArray(), algorithm), label + ": " + (head ? "head -c" : "dd") + " " + size + " bytes, " + algorithm + hashError.Trim());
                }
            }
        }

        string ram = Path.Combine(_work, "ram-" + Guid.NewGuid().ToString("N") + ".bin");
        exit = RunWithInput(busybox, MtdFlash.BuildUploadCommand(ram, false), content, out _, out string uploadError);
        Check(exit == 0 && File.ReadAllBytes(ram).SequenceEqual(content), label + ": the upload stores the standard input (" + uploadError.Trim() + ")");
        exit = RunWithInput(busybox, MtdFlash.BuildUploadCommand(ram, false), new byte[] { 1, 2, 3 }, out _, out _);
        Check(exit != 0 && File.ReadAllBytes(ram).SequenceEqual(content), label + ": an existing file is never replaced (noclobber)");
        Run(busybox, MtdFlash.BuildFileHashCommand(ram, "sha256", false), out byte[] fileHash, out _);
        Equal(MtdFlash.ParseHash(Lines(fileHash), "sha256"), Hash(content, "sha256"), label + ": the checksum of the file in RAM");
        Run(busybox, MtdFlash.BuildRemoveCommand(ram, false), out _, out _);
        Check(!File.Exists(ram), label + ": the image in RAM is removed");
    }

    /// <summary>Makes the fake flash tools: they write into the "device" file the way the real ones write into the partition.</summary>
    private static string MakeTools(string mode)
    {
        string bin = Path.Combine(_work, "bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        // mtd write IMAGE DEVICE (OpenWrt); "corrupt" writes one byte wrong, "fail" fails after writing half.
        string body = mode switch
        {
            "corrupt" => "dd if=\"$2\" of=\"$3\" conv=notrunc 2>/dev/null; printf 'X' | dd of=\"$3\" bs=1 seek=100 conv=notrunc 2>/dev/null; echo \"Writing from $2 to $3 ...\"",
            "fail" => "echo 'Could not open mtd device: Permission denied' >&2; exit 1",
            _ => "[ \"$1\" = write ] || exit 2; echo \"Unlocking $3 ...\"; echo \"Writing from $2 to $3 ...\"; dd if=\"$2\" of=\"$3\" conv=notrunc 2>/dev/null"
        };
        WriteScript(Path.Combine(bin, "mtd"), body);
        return bin;
    }

    private static void MakeMtdUtils(string bin)
    {
        // flashcp IMAGE DEVICE; flash_erase DEVICE 0 0 fills it with 0xff; nandwrite -p DEVICE IMAGE.
        WriteScript(Path.Combine(bin, "flashcp"), "dd if=\"$1\" of=\"$2\" conv=notrunc 2>/dev/null");
        WriteScript(Path.Combine(bin, "flash_erase"), "s=$(wc -c < \"$1\"); tr '\\000' '\\377' < /dev/zero | dd of=\"$1\" bs=1 count=$s conv=notrunc 2>/dev/null");
        WriteScript(Path.Combine(bin, "nandwrite"), "[ \"$1\" = -p ] || exit 2; dd if=\"$3\" of=\"$2\" conv=notrunc 2>/dev/null");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void WriteScript(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void JobTests(string label, string busybox)
    {
        string ramDir = Path.Combine(_work, "ram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ramDir);

        // The backup of a partition.
        byte[] flash = RandomBytes(4 * 65536, 2);
        string device = Path.Combine(_work, "mtd3-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(device, flash);
        MtdPartition partition = new MtdPartition { Index = 3, Name = "firmware", Size = flash.Length, EraseSize = 65536, Offset = 0x50000, Device = device, Type = "nor", Flags = 0xc00 };
        MtdHostInfo info = new MtdHostInfo { MemAvailableBytes = 64L * 1024 * 1024, HeadHasBytes = true };
        info.Tools.Add("sha256sum");
        info.Tools.Add("md5sum");
        info.Tools.Add("mtd");
        info.RamDirectories.Add(new MtdRamDirectory { Path = ramDir, Type = "tmpfs", FreeBytes = 32L * 1024 * 1024 });
        LocalShell shell = new LocalShell(busybox, MakeTools("good"));
        MemoryStream image = new MemoryStream();
        List<string> log = new List<string>();
        MtdImagePart part = MtdBackupJob.BackUp(shell, info, partition, image, "b.bin", 0, false, null, null, null, log.Add);
        Check(image.ToArray().SequenceEqual(flash) && part.Sha256 == Hash(flash, "sha256") && part.Md5 == Hash(flash, "md5") && part.Name == "firmware" && part.FlashOffset == 0x50000, label + " backup: the image, its checksums and the description");
        Check(log.Any(l => l.Contains("matches the flash")), label + " backup: compared with the checksum of the flash on the server");

        // A flash that reads differently the second time.
        ChangingShell changing = new ChangingShell(shell, device);
        Expect(() => MtdBackupJob.BackUp(changing, info, partition, new MemoryStream(), "b.bin", 0, false, null, null, null, null), "differs from the flash", label + " backup: a flash that reads differently is not trusted");
        File.WriteAllBytes(device, flash);
        // A partition that is shorter than /proc/mtd says (a read error in the middle).
        MtdPartition longer = new MtdPartition { Index = 3, Name = "firmware", Size = flash.Length + 65536, Device = device };
        Expect(() => MtdBackupJob.BackUp(shell, info, longer, new MemoryStream(), "b.bin", 0, false, null, null, null, null), "instead of", label + " backup: a short read is an error");

        // The restore of an image: with mtd, then with flashcp, then with flash_erase + nandwrite.
        foreach (string toolSet in new[] { "mtd", "flashcp", "nandwrite" })
        {
            byte[] newImage = RandomBytes(3 * 65536, 3);
            File.WriteAllBytes(device, flash);
            MtdHostInfo restoreInfo = new MtdHostInfo { MemAvailableBytes = 64L * 1024 * 1024, HeadHasBytes = toolSet != "flashcp" };
            restoreInfo.Tools.Add("sha256sum");
            restoreInfo.RamDirectories.Add(new MtdRamDirectory { Path = ramDir, Type = "tmpfs", FreeBytes = 32L * 1024 * 1024 });
            string bin = MakeTools("good");
            if (toolSet == "mtd")
            {
                restoreInfo.Tools.Add("mtd");
            }
            else
            {
                MakeMtdUtils(bin);
                restoreInfo.Tools.Add(toolSet);
                if (toolSet == "nandwrite")
                {
                    restoreInfo.Tools.Add("flash_erase");
                }
            }
            MtdPartition target = new MtdPartition { Index = 3, Name = "firmware", Size = flash.Length, EraseSize = 65536, Device = device, Type = toolSet == "nandwrite" ? "nand" : "nor", Flags = 0xc00 };
            MtdImagePart imagePart = new MtdImagePart { File = "x", Size = newImage.Length, Name = "firmware", Sha256 = Hash(newImage, "sha256"), Md5 = Hash(newImage, "md5") };
            List<MtdRestoreStep> steps = new List<MtdRestoreStep>();
            List<string> restoreLog = new List<string>();
            MtdRestoreJob.Restore(new LocalShell(busybox, bin), () => restoreInfo, imagePart, () => new MemoryStream(newImage), target, false, "t1", (step, text) => steps.Add(step), null, null, restoreLog.Add);
            byte[] after = File.ReadAllBytes(device);
            Check(after.Take(newImage.Length).SequenceEqual(newImage), label + " restore with " + toolSet + ": the partition holds the image");
            if (toolSet == "nandwrite")
            {
                Check(after.Skip(newImage.Length).All(x => x == 0xFF), label + " restore with nandwrite: the rest of the partition is erased");
            }
            Check(steps.Distinct().SequenceEqual(new[] { MtdRestoreStep.CheckingImage, MtdRestoreStep.CheckingMemory, MtdRestoreStep.Uploading, MtdRestoreStep.CheckingUpload, MtdRestoreStep.Writing, MtdRestoreStep.CheckingFlash, MtdRestoreStep.CleaningUp }), label + " restore with " + toolSet + ": every step in order");
            Check(restoreLog.Any(l => l.Contains("written and read back")) && Directory.GetFiles(ramDir).Length == 0, label + " restore with " + toolSet + ": read back from the flash, the image in RAM removed");
        }

        // Failures: the partition must stay as it was unless the write itself went wrong, and the RAM file is always removed.
        byte[] good = RandomBytes(2 * 65536, 4);
        MtdPartition flashPart = new MtdPartition { Index = 3, Name = "firmware", Size = flash.Length, Device = device, Type = "nor", Flags = 0xc00 };
        MtdImagePart goodPart = new MtdImagePart { Size = good.Length, Name = "firmware", Sha256 = Hash(good, "sha256") };

        File.WriteAllBytes(device, flash);
        MtdImagePart damaged = new MtdImagePart { Size = good.Length, Name = "firmware", Sha256 = new string('0', 64) };
        Expect(() => MtdRestoreJob.Restore(shell, () => info, damaged, () => new MemoryStream(good), flashPart, false, "t2", null, null, null, null), "damaged", label + " restore: a damaged image file stops it");
        Check(File.ReadAllBytes(device).SequenceEqual(flash), label + " restore: a damaged image leaves the partition alone");

        MtdHostInfo small = new MtdHostInfo { MemAvailableBytes = 8L * 1024 * 1024 };
        small.Tools.Add("sha256sum");
        small.Tools.Add("mtd");
        small.RamDirectories.Add(new MtdRamDirectory { Path = ramDir, Type = "tmpfs", FreeBytes = 32L * 1024 * 1024 });
        Expect(() => MtdRestoreJob.Restore(shell, () => small, goodPart, () => new MemoryStream(good), flashPart, false, "t3", null, null, null, null), "memory", label + " restore: too little memory stops it");
        Check(File.ReadAllBytes(device).SequenceEqual(flash), label + " restore: too little memory leaves the partition alone");

        Expect(() => MtdRestoreJob.Restore(new CorruptingShell(shell), () => info, goodPart, () => new MemoryStream(good), flashPart, false, "t4", null, null, null, null), "in RAM is not", label + " restore: an image damaged on the way is found in RAM");
        Check(File.ReadAllBytes(device).SequenceEqual(flash) && Directory.GetFiles(ramDir).Length == 0, label + " restore: then nothing is written and the RAM file is removed");

        Expect(() => MtdRestoreJob.Restore(new LocalShell(busybox, MakeTools("fail")), () => info, goodPart, () => new MemoryStream(good), flashPart, false, "t5", null, null, null, null), "Permission denied", label + " restore: a failing write tool is reported with its message");
        Check(Directory.GetFiles(ramDir).Length == 0, label + " restore: the RAM file is removed after a failed write");

        Expect(() => MtdRestoreJob.Restore(new LocalShell(busybox, MakeTools("corrupt")), () => info, goodPart, () => new MemoryStream(good), flashPart, false, "t6", null, null, null, null), "does not hold the image", label + " restore: a write that left wrong data is found by the read-back");

        MtdPartition readOnly = new MtdPartition { Index = 3, Name = "firmware", Size = flash.Length, Device = device, Type = "rom" };
        Expect(() => MtdRestoreJob.Restore(shell, () => info, goodPart, () => new MemoryStream(good), readOnly, false, "t7", null, null, null, null), "not written as raw flash", label + " restore: a partition that cannot be written is refused before the upload");
        Check(Directory.GetFiles(ramDir).Length == 0, label + " restore: no RAM file left behind at all");
    }

    /// <summary>The command with the quoting of the outer single quotes undone.</summary>
    private static string Plain(string command)
    {
        return command.Replace("'\\''", "'");
    }

    private static void Expect(Action action, string text, string name)
    {
        try
        {
            action();
        }
        catch (MtdJobException exception)
        {
            Check(exception.Message.Contains(text), name + " (" + exception.Message + ")");
            return;
        }
        Check(false, name + " (no error)");
    }

    /// <summary>Runs the commands with /bin/sh or BusyBox sh, the fake tools first in PATH.</summary>
    private sealed class LocalShell : IRemoteShell
    {
        private readonly string _busybox;

        private readonly string _bin;

        public LocalShell(string busybox, string bin)
        {
            _busybox = busybox;
            _bin = bin;
        }

        public RemoteCommandResult Run(string command)
        {
            int exit = Program.Run(_busybox, command, out byte[] output, out string error, _bin);
            return new RemoteCommandResult { ExitStatus = exit, Output = Lines(output).ToList(), Error = error };
        }

        public RemoteCommandResult RunWithInput(string command, Stream input, Action<long> progress, Func<bool> isCancelled)
        {
            MemoryStream data = new MemoryStream();
            input.CopyTo(data);
            int exit = Program.RunWithInput(_busybox, command, data.ToArray(), out byte[] output, out string error, _bin);
            return new RemoteCommandResult { ExitStatus = exit, Output = Lines(output).ToList(), Error = error, Bytes = data.Length };
        }

        public RemoteCommandResult RunToStream(string command, Stream output, Action<long> progress, Func<bool> isCancelled)
        {
            int exit = Program.Run(_busybox, command, out byte[] data, out string error, _bin);
            output.Write(data, 0, data.Length);
            return new RemoteCommandResult { ExitStatus = exit, Error = error, Bytes = data.Length };
        }
    }

    /// <summary>Changes the "flash" right after it was read, as unstable flash would read differently.</summary>
    private sealed class ChangingShell : IRemoteShell
    {
        private readonly IRemoteShell _inner;

        private readonly string _device;

        public ChangingShell(IRemoteShell inner, string device)
        {
            _inner = inner;
            _device = device;
        }

        public RemoteCommandResult Run(string command) => _inner.Run(command);

        public RemoteCommandResult RunWithInput(string command, Stream input, Action<long> progress, Func<bool> isCancelled) => _inner.RunWithInput(command, input, progress, isCancelled);

        public RemoteCommandResult RunToStream(string command, Stream output, Action<long> progress, Func<bool> isCancelled)
        {
            RemoteCommandResult result = _inner.RunToStream(command, output, progress, isCancelled);
            byte[] data = File.ReadAllBytes(_device);
            data[10] ^= 0xFF;
            File.WriteAllBytes(_device, data);
            return result;
        }
    }

    /// <summary>Flips a byte of the image on its way to the server.</summary>
    private sealed class CorruptingShell : IRemoteShell
    {
        private readonly IRemoteShell _inner;

        public CorruptingShell(IRemoteShell inner)
        {
            _inner = inner;
        }

        public RemoteCommandResult Run(string command) => _inner.Run(command);

        public RemoteCommandResult RunToStream(string command, Stream output, Action<long> progress, Func<bool> isCancelled) => _inner.RunToStream(command, output, progress, isCancelled);

        public RemoteCommandResult RunWithInput(string command, Stream input, Action<long> progress, Func<bool> isCancelled)
        {
            MemoryStream data = new MemoryStream();
            input.CopyTo(data);
            byte[] bytes = data.ToArray();
            bytes[bytes.Length / 2] ^= 0x01;
            return _inner.RunWithInput(command, new MemoryStream(bytes), progress, isCancelled);
        }
    }

    private static byte[] RandomBytes(int count, int seed)
    {
        byte[] data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Hash(byte[] data, string algorithm)
    {
        using HashAlgorithm hash = algorithm == "sha256" ? SHA256.Create() : MD5.Create();
        return TarArchiveReader.ToHex(hash.ComputeHash(data));
    }

    private static string[] Lines(byte[] output)
    {
        return Encoding.UTF8.GetString(output).Split('\n');
    }

    /// <summary>"/bin/sh -c '...'" as it is, or with BusyBox sh in place of /bin/sh.</summary>
    private static ProcessStartInfo Start(string busybox, string command, string pathPrefix)
    {
        ProcessStartInfo start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(busybox != null && command.StartsWith("/bin/sh -c ", StringComparison.Ordinal) ? busybox + " sh -c " + command.Substring("/bin/sh -c ".Length) : command);
        string path = Environment.GetEnvironmentVariable("PATH");
        if (busybox != null)
        {
            path = Applets(busybox) + ":" + path;
        }
        if (pathPrefix != null)
        {
            path = pathPrefix + ":" + path;
        }
        start.Environment["PATH"] = path;
        return start;
    }

    private static string _applets;

    /// <summary>A folder with links to the BusyBox applets, so that cat, head, dd, sha256sum and md5sum are those of BusyBox.</summary>
    private static string Applets(string busybox)
    {
        if (_applets == null)
        {
            _applets = Path.Combine(_work, "applets");
            Directory.CreateDirectory(_applets);
            foreach (string applet in new[] { "cat", "head", "dd", "sha256sum", "md5sum", "rm", "df", "id", "wc", "tr", "printf" })
            {
                File.CreateSymbolicLink(Path.Combine(_applets, applet), busybox);
            }
        }
        return _applets;
    }

    internal static int Run(string busybox, string command, out byte[] stdout, out string stderr, string pathPrefix = null)
    {
        return RunWithInput(busybox, command, new byte[0], out stdout, out stderr, pathPrefix);
    }

    internal static int RunWithInput(string busybox, string command, byte[] input, out byte[] stdout, out string stderr, string pathPrefix = null)
    {
        using Process process = Process.Start(Start(busybox, command, pathPrefix));
        System.Threading.Tasks.Task<string> errorTask = process.StandardError.ReadToEndAsync();
        MemoryStream output = new MemoryStream();
        System.Threading.Tasks.Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        try
        {
            process.StandardInput.BaseStream.Write(input, 0, input.Length);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        outputTask.Wait();
        process.WaitForExit();
        stdout = output.ToArray();
        stderr = errorTask.Result;
        return process.ExitCode;
    }
}
