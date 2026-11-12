using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Transport;

/// <summary>A partition of the flash, as /proc/mtd and /sys/class/mtd describe it.</summary>
public sealed class MtdPartition
{
    /// <summary>N of mtdN.</summary>
    public int Index { get; set; }

    public string Name { get; set; }

    public long Size { get; set; }

    public long EraseSize { get; set; }

    /// <summary>The offset in the flash chip (/sys/class/mtd/mtdN/offset); null on kernels that do not tell.</summary>
    public long? Offset { get; set; }

    /// <summary>"nor", "nand", "mlc-nand", "ubi", "rom", "absent"... (/sys/class/mtd/mtdN/type); null when unknown.</summary>
    public string Type { get; set; }

    /// <summary>The flags of the partition (/sys/class/mtd/mtdN/flags); null when unknown.</summary>
    public long? Flags { get; set; }

    /// <summary>The character device (/dev/mtdN) or, when there is none, the block device (/dev/mtdblockN); null when neither exists.</summary>
    public string Device { get; set; }

    /// <summary>The device is the character device, which is what the write tools need.</summary>
    public bool HasCharDevice
    {
        get
        {
            string name = Device == null ? null : Device.Substring(Device.LastIndexOf('/') + 1);
            return name != null && name.StartsWith("mtd", StringComparison.Ordinal) && !name.StartsWith("mtdblock", StringComparison.Ordinal);
        }
    }

    /// <summary>Where the partition is in use (a mounted mtdblock, an attached UBI device); null when it is not.</summary>
    public string InUse { get; set; }

    /// <summary>MTD_WRITEABLE (0x400): false for a partition the kernel keeps read-only; null when the flags are unknown.</summary>
    public bool? Writable => Flags.HasValue ? (Flags.Value & 0x400) != 0 : (bool?)null;

    public bool IsNand => Type != null && Type.IndexOf("nand", StringComparison.OrdinalIgnoreCase) >= 0;

    public string Label => "mtd" + Index.ToString(CultureInfo.InvariantCulture) + " \"" + Name + "\"";

    public override string ToString()
    {
        return Label;
    }
}

/// <summary>A directory of the server whose files are kept in memory (tmpfs or ramfs): where an image waits before it is written.</summary>
public sealed class MtdRamDirectory
{
    public string Path { get; set; }

    public string Type { get; set; }

    /// <summary>The free space of the file system (df); null for ramfs, which has no limit but the memory.</summary>
    public long? FreeBytes { get; set; }
}

/// <summary>What the flash window asks the server once it opens.</summary>
public sealed class MtdHostInfo
{
    public List<MtdPartition> Partitions { get; } = new List<MtdPartition>();

    public bool? IsRoot { get; set; }

    public bool CanSudo { get; set; }

    /// <summary>The programs found: mtd, flashcp, flash_erase, nandwrite, sha256sum, md5sum.</summary>
    public HashSet<string> Tools { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>head knows -c (BusyBox may be built without it): the read-back uses head, otherwise dd.</summary>
    public bool HeadHasBytes { get; set; }

    public long? MemAvailableBytes { get; set; }

    public long? MemFreeBytes { get; set; }

    public long? CachedBytes { get; set; }

    public List<MtdRamDirectory> RamDirectories { get; } = new List<MtdRamDirectory>();

    /// <summary>The checksum the server can compute: sha256 when it has sha256sum, md5 when it has md5sum only, null otherwise.</summary>
    public string HashAlgorithm => Tools.Contains("sha256sum") ? "sha256" : Tools.Contains("md5sum") ? "md5" : null;

    /// <summary>The memory that can be taken without pushing anything out: MemAvailable, or MemFree and the page cache on old kernels.</summary>
    public long? UsableMemoryBytes => MemAvailableBytes ?? (MemFreeBytes.HasValue ? MemFreeBytes + (CachedBytes ?? 0) : null);
}

/// <summary>A piece of an image file on this computer that holds one partition (a whole file, or a part of a full flash image).</summary>
public sealed class MtdImagePart
{
    /// <summary>The image file, relative to the manifest (or absolute for a bare image).</summary>
    public string File { get; set; }

    public long FileOffset { get; set; }

    public long Size { get; set; }

    /// <summary>The partition it was read from: its number and name; -1 and null for an image without a manifest.</summary>
    public int MtdIndex { get; set; } = -1;

    public string Name { get; set; }

    public long? FlashOffset { get; set; }

    public long EraseSize { get; set; }

    public string Type { get; set; }

    public string Sha256 { get; set; }

    public string Md5 { get; set; }
}

/// <summary>The description of a flash backup (name.mtdinfo next to the images): which partitions, where in which file, their checksums.</summary>
public sealed class MtdManifest
{
    public const string Signature = "SmarTTY-MTD-backup 1";

    public string Host { get; set; }

    public DateTime Created { get; set; }

    /// <summary>"partitions" (a file per partition) or "fullflash" (one file with the whole flash).</summary>
    public string Kind { get; set; }

    public List<MtdImagePart> Parts { get; } = new List<MtdImagePart>();

    public string Write()
    {
        StringBuilder text = new StringBuilder();
        text.Append(Signature).Append('\n');
        text.Append("host\t").Append(Clean(Host)).Append('\n');
        text.Append("created\t").Append(Created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("kind\t").Append(Clean(Kind)).Append('\n');
        foreach (MtdImagePart part in Parts)
        {
            text.Append("part");
            Field(text, "file", part.File);
            Field(text, "fileoffset", part.FileOffset.ToString(CultureInfo.InvariantCulture));
            Field(text, "size", part.Size.ToString(CultureInfo.InvariantCulture));
            Field(text, "mtd", part.MtdIndex.ToString(CultureInfo.InvariantCulture));
            Field(text, "name", part.Name);
            if (part.FlashOffset.HasValue)
            {
                Field(text, "flashoffset", part.FlashOffset.Value.ToString(CultureInfo.InvariantCulture));
            }
            Field(text, "erasesize", part.EraseSize.ToString(CultureInfo.InvariantCulture));
            Field(text, "type", part.Type);
            Field(text, "sha256", part.Sha256);
            Field(text, "md5", part.Md5);
            text.Append('\n');
        }
        return text.ToString();
    }

    private static void Field(StringBuilder text, string key, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            text.Append('\t').Append(key).Append('=').Append(Clean(value));
        }
    }

    private static string Clean(string value)
    {
        return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>Reads a manifest; throws <see cref="InvalidDataException"/> for a file that is not one.</summary>
    public static MtdManifest Parse(string text)
    {
        string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Signature)
        {
            throw new InvalidDataException("This is not a flash backup description of SmarTTY (the first line is not \"" + Signature + "\").");
        }
        MtdManifest manifest = new MtdManifest();
        foreach (string line in lines.Skip(1))
        {
            string[] fields = line.Split('\t');
            switch (fields[0])
            {
                case "host":
                    manifest.Host = fields.Length > 1 ? fields[1] : null;
                    break;
                case "created":
                    if (fields.Length > 1 && DateTime.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime created))
                    {
                        manifest.Created = created;
                    }
                    break;
                case "kind":
                    manifest.Kind = fields.Length > 1 ? fields[1] : null;
                    break;
                case "part":
                    manifest.Parts.Add(ParsePart(fields));
                    break;
            }
        }
        if (manifest.Parts.Count == 0)
        {
            throw new InvalidDataException("The flash backup description lists no partitions.");
        }
        return manifest;
    }

    private static MtdImagePart ParsePart(string[] fields)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string field in fields.Skip(1))
        {
            int equals = field.IndexOf('=');
            if (equals > 0)
            {
                values[field.Substring(0, equals)] = field.Substring(equals + 1);
            }
        }
        long Number(string key, long fallback = 0)
        {
            return values.TryGetValue(key, out string text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : fallback;
        }
        if (!values.TryGetValue("file", out string file) || file.Length == 0 || Number("size", -1) <= 0)
        {
            throw new InvalidDataException("A partition of the flash backup description has no file or size.");
        }
        return new MtdImagePart
        {
            File = file,
            FileOffset = Number("fileoffset"),
            Size = Number("size"),
            MtdIndex = (int)Number("mtd", -1),
            Name = values.TryGetValue("name", out string name) ? name : null,
            FlashOffset = values.ContainsKey("flashoffset") ? Number("flashoffset") : (long?)null,
            EraseSize = Number("erasesize"),
            Type = values.TryGetValue("type", out string type) ? type : null,
            Sha256 = values.TryGetValue("sha256", out string sha) ? sha.ToLowerInvariant() : null,
            Md5 = values.TryGetValue("md5", out string md5) ? md5.ToLowerInvariant() : null
        };
    }
}

/// <summary>
/// Tools > Flash (MTD) backup and restore: the shell commands for the flash partitions of /proc/mtd and the rules around them.
/// A backup reads /dev/mtdN with cat, whose output the SSH channel carries into a file on this computer (nothing is written on the
/// server), and compares the checksum the server computes from the flash with the one computed here. A restore checks the image
/// against its stored checksum here, checks that the server has the memory for it, copies it into a RAM file system (tmpfs) on the
/// server, checks it there, writes it with the flash tool of the system (mtd of OpenWrt, flashcp, or flash_erase and nandwrite)
/// and reads it back from the flash for a last check: only then does the write count as done. All of it is POSIX shell with the
/// tools of BusyBox. Pure logic without WPF or SSH, tested by tests/MtdFlashTests.
/// </summary>
public static class MtdFlash
{
    /// <summary>Printed by <see cref="ProbeCommand"/> as "SMARTTY_MTD=yes" or "SMARTTY_MTD=no".</summary>
    public const string ProbeMarker = "SMARTTY_MTD=";

    /// <summary>The part of the host probe that tells whether the host has flash partitions (a line "mtdN:" in /proc/mtd).</summary>
    public const string ProbeCommand = "if grep -qs '^mtd[0-9]' /proc/mtd; then echo " + ProbeMarker + "yes; else echo " + ProbeMarker + "no; fi";

    /// <summary>True or false from the output of <see cref="ProbeCommand"/>; null when the output has no answer.</summary>
    public static bool? FromProbe(IEnumerable<string> lines)
    {
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = rawLine?.Trim();
            if (line == ProbeMarker + "yes")
            {
                return true;
            }
            if (line == ProbeMarker + "no")
            {
                return false;
            }
        }
        return null;
    }

    /// <summary>The programs whose presence matters: the write tools, the checksum tools.</summary>
    public static readonly string[] ToolNames = { "mtd", "flashcp", "flash_erase", "nandwrite", "sha256sum", "md5sum" };

    /// <summary>
    /// Prints everything the window needs, one marked line per fact: the uid and passwordless sudo, the lines of /proc/mtd, the
    /// offset, type and flags of every partition from sysfs, its device node, the partitions in use (mounted mtdblock, attached UBI),
    /// the tools, the memory (/proc/meminfo) and the free space of the RAM file systems. Shell built-ins, cat and df only.
    /// </summary>
    public static readonly string InfoCommand = string.Join("; ", new[]
    {
        "echo SMARTTY_UID=$(id -u 2>/dev/null)",
        "if [ \"$(id -u 2>/dev/null)\" != 0 ] && command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then echo SMARTTY_SUDO=yes; fi",
        "if [ -r /proc/mtd ]; then while IFS= read -r line; do case \"$line\" in mtd[0-9]*:*) n=${line%%:*}; echo \"SMARTTY_MTD $line\"; " +
            "if [ -c /dev/$n ]; then echo \"SMARTTY_DEV $n /dev/$n\"; elif [ -b /dev/mtdblock${n#mtd} ]; then echo \"SMARTTY_DEV $n /dev/mtdblock${n#mtd}\"; fi; " +
            "s=/sys/class/mtd/$n; if [ -d $s ]; then echo \"SMARTTY_SYS $n offset=$(cat $s/offset 2>/dev/null) type=$(cat $s/type 2>/dev/null) flags=$(cat $s/flags 2>/dev/null)\"; fi;; esac; done < /proc/mtd; fi",
        "for t in " + string.Join(" ", ToolNames) + "; do if command -v $t >/dev/null 2>&1; then echo \"SMARTTY_TOOL $t\"; fi; done",
        "if head -c 1 /dev/null >/dev/null 2>&1; then echo SMARTTY_HEADC=yes; fi",
        "if [ -r /proc/meminfo ]; then while read -r k v rest; do case \"$k\" in MemAvailable:|MemFree:|Cached:) echo \"SMARTTY_MEM ${k%:} $v\";; esac; done < /proc/meminfo; fi",
        "if [ -r /proc/mounts ]; then while read -r dv mp ty rest; do " +
            "case \"$dv\" in /dev/mtdblock[0-9]*) echo \"SMARTTY_INUSE mtd${dv#/dev/mtdblock} mounted on $mp ($ty)\";; esac; " +
            "case \"$ty\" in tmpfs|ramfs) case \"$mp\" in *\\\\*) ;; *) if [ -d \"$mp\" ]; then f=$(LC_ALL=C df -P -k \"$mp\" 2>/dev/null | while read -r a b c avail rest; do case \"$avail\" in [0-9]*) echo $avail;; esac; done); echo \"SMARTTY_RAM $ty ${f:--} $mp\"; fi;; esac;; esac; " +
            "done < /proc/mounts; fi",
        "for u in /sys/class/ubi/ubi[0-9]*; do if [ -r $u/mtd_num ]; then echo \"SMARTTY_INUSE mtd$(cat $u/mtd_num) attached to UBI (${u##*/})\"; fi; done"
    });

    public static MtdHostInfo ParseInfo(IEnumerable<string> lines)
    {
        MtdHostInfo info = new MtdHostInfo();
        Dictionary<int, MtdPartition> byIndex = new Dictionary<int, MtdPartition>();
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = rawLine?.TrimEnd('\r');
            if (line == null)
            {
                continue;
            }
            if (line.StartsWith("SMARTTY_UID=", StringComparison.Ordinal))
            {
                string uid = line.Substring(12).Trim();
                if (uid.Length > 0 && uid.All(char.IsDigit))
                {
                    info.IsRoot = uid == "0";
                }
            }
            else if (line.Trim() == "SMARTTY_SUDO=yes")
            {
                info.CanSudo = true;
            }
            else if (line.Trim() == "SMARTTY_HEADC=yes")
            {
                info.HeadHasBytes = true;
            }
            else if (line.StartsWith("SMARTTY_MTD ", StringComparison.Ordinal))
            {
                MtdPartition partition = ParseProcMtdLine(line.Substring(12));
                if (partition != null && !byIndex.ContainsKey(partition.Index))
                {
                    byIndex[partition.Index] = partition;
                    info.Partitions.Add(partition);
                }
            }
            else if (line.StartsWith("SMARTTY_DEV ", StringComparison.Ordinal))
            {
                string[] parts = line.Substring(12).Split(new[] { ' ' }, 2);
                if (parts.Length == 2 && TryIndex(parts[0], out int index) && byIndex.TryGetValue(index, out MtdPartition partition))
                {
                    partition.Device = parts[1].Trim();
                }
            }
            else if (line.StartsWith("SMARTTY_SYS ", StringComparison.Ordinal))
            {
                string[] parts = line.Substring(12).Split(' ');
                if (parts.Length >= 1 && TryIndex(parts[0], out int index) && byIndex.TryGetValue(index, out MtdPartition partition))
                {
                    foreach (string part in parts.Skip(1))
                    {
                        int equals = part.IndexOf('=');
                        if (equals <= 0)
                        {
                            continue;
                        }
                        string key = part.Substring(0, equals);
                        string value = part.Substring(equals + 1).Trim();
                        if (key == "offset")
                        {
                            partition.Offset = ParseNumber(value);
                        }
                        else if (key == "type" && value.Length > 0)
                        {
                            partition.Type = value;
                        }
                        else if (key == "flags")
                        {
                            partition.Flags = ParseNumber(value);
                        }
                    }
                }
            }
            else if (line.StartsWith("SMARTTY_TOOL ", StringComparison.Ordinal))
            {
                info.Tools.Add(line.Substring(13).Trim());
            }
            else if (line.StartsWith("SMARTTY_MEM ", StringComparison.Ordinal))
            {
                string[] parts = line.Substring(12).Split(' ');
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kib))
                {
                    long bytes = kib * 1024;
                    switch (parts[0])
                    {
                        case "MemAvailable":
                            info.MemAvailableBytes = bytes;
                            break;
                        case "MemFree":
                            info.MemFreeBytes = bytes;
                            break;
                        case "Cached":
                            info.CachedBytes = bytes;
                            break;
                    }
                }
            }
            else if (line.StartsWith("SMARTTY_RAM ", StringComparison.Ordinal))
            {
                string[] parts = line.Substring(12).Split(new[] { ' ' }, 3);
                if (parts.Length == 3 && parts[2].StartsWith("/", StringComparison.Ordinal))
                {
                    long? free = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kib) ? kib * 1024 : (long?)null;
                    info.RamDirectories.Add(new MtdRamDirectory { Type = parts[0], FreeBytes = parts[0] == "ramfs" ? null : free, Path = parts[2] });
                }
            }
            else if (line.StartsWith("SMARTTY_INUSE ", StringComparison.Ordinal))
            {
                string[] parts = line.Substring(14).Split(new[] { ' ' }, 2);
                if (parts.Length == 2 && TryIndex(parts[0], out int index) && byIndex.TryGetValue(index, out MtdPartition partition))
                {
                    partition.InUse = partition.InUse == null ? parts[1] : partition.InUse + "; " + parts[1];
                }
            }
        }
        return info;
    }

    /// <summary>A line of /proc/mtd: <c>mtd3: 00fb0000 00010000 "firmware"</c> (sizes in hexadecimal).</summary>
    public static MtdPartition ParseProcMtdLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }
        int colon = line.IndexOf(':');
        if (colon < 4 || !TryIndex(line.Substring(0, colon), out int index))
        {
            return null;
        }
        string[] fields = line.Substring(colon + 1).Trim().Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3
            || !long.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long size)
            || !long.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long erase))
        {
            return null;
        }
        string name = fields[2].Trim();
        if (name.Length >= 2 && name[0] == '"' && name[name.Length - 1] == '"')
        {
            name = name.Substring(1, name.Length - 2);
        }
        return new MtdPartition { Index = index, Size = size, EraseSize = erase, Name = name };
    }

    private static bool TryIndex(string device, out int index)
    {
        index = -1;
        return device != null && device.StartsWith("mtd", StringComparison.Ordinal)
            && int.TryParse(device.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>"0x40000" or "262144".</summary>
    private static long? ParseNumber(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) ? hex : (long?)null;
        }
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : (long?)null;
    }

    /// <summary>
    /// The partitions that make up the whole flash, in their order: the ones that do not lie inside another one ("kernel" and
    /// "rootfs" inside "firmware" are left out, so is everything when the kernel shows the whole chip as a partition), which must
    /// start at 0 and follow one another without gaps or overlaps. Null with the reason when that cannot be told or is not so.
    /// </summary>
    public static List<MtdPartition> FullFlashLayout(IList<MtdPartition> partitions, out string reason)
    {
        reason = null;
        List<MtdPartition> usable = (partitions ?? new List<MtdPartition>()).Where(p => p.Size > 0).ToList();
        if (usable.Count == 0)
        {
            reason = "the server lists no flash partitions";
            return null;
        }
        if (usable.Any(p => !p.Offset.HasValue))
        {
            reason = "the kernel does not tell where the partitions lie (no /sys/class/mtd/mtdN/offset): back them up one by one";
            return null;
        }
        List<MtdPartition> sorted = usable.OrderBy(p => p.Offset.Value).ThenByDescending(p => p.Size).ThenBy(p => p.Index).ToList();
        List<MtdPartition> layout = new List<MtdPartition>();
        foreach (MtdPartition partition in sorted)
        {
            long start = partition.Offset.Value;
            long end = start + partition.Size;
            if (layout.Any(k => start >= k.Offset.Value && end <= k.Offset.Value + k.Size))
            {
                // Inside a partition that is already part of the image.
                continue;
            }
            MtdPartition last = layout.LastOrDefault();
            long lastEnd = last == null ? 0 : last.Offset.Value + last.Size;
            if (start < lastEnd)
            {
                reason = partition.Label + " overlaps " + last.Label + " without lying inside it";
                return null;
            }
            if (start > lastEnd)
            {
                reason = "no partition covers 0x" + lastEnd.ToString("x", CultureInfo.InvariantCulture) + "-0x" + start.ToString("x", CultureInfo.InvariantCulture) + " of the flash, so it cannot be read";
                return null;
            }
            layout.Add(partition);
        }
        // Several chips (two NOR chips, a NOR and a NAND) each have their own offsets starting at 0.
        if (layout.Select(p => p.Type ?? "").Distinct().Count() > 1)
        {
            reason = "the partitions belong to flash chips of different types (" + string.Join(", ", layout.Select(p => p.Type).Distinct()) + "): back them up one by one";
            return null;
        }
        return layout;
    }

    private static string WrapForUser(string script, bool useSudo)
    {
        string command = SmarTTY.Utilities.PosixShell.Wrap(script, addAdminDirectoriesToPath: true);
        return useSudo ? "sudo -n " + command : command;
    }

    /// <summary>The command line that writes the whole partition to its standard output (the backup), and nothing else.</summary>
    public static string BuildReadCommand(MtdPartition partition, bool useSudo)
    {
        RequireDevice(partition);
        return WrapForUser("exec cat " + RemotePathUtils.ShellQuote(partition.Device), useSudo);
    }

    private static void RequireDevice(MtdPartition partition)
    {
        if (partition?.Device == null)
        {
            throw new InvalidOperationException("The server has no device node for " + partition?.Label + " (neither /dev/mtdN nor /dev/mtdblockN).");
        }
    }

    /// <summary>The checksum program of an algorithm ("sha256" or "md5").</summary>
    public static string HashProgram(string algorithm)
    {
        switch (algorithm)
        {
            case "sha256":
                return "sha256sum";
            case "md5":
                return "md5sum";
            default:
                throw new ArgumentException("Unknown checksum: " + algorithm, nameof(algorithm));
        }
    }

    /// <summary>
    /// The command line that prints the checksum of the first <paramref name="size"/> bytes of <paramref name="device"/>: the read-back
    /// after a write, and the check of a backup. head -c when it knows it, otherwise dd with the biggest block size that divides
    /// the size (a partition is a whole number of erase blocks, so that is 64 KiB or close).
    /// </summary>
    public static string BuildDeviceHashCommand(string device, long size, string algorithm, bool headHasBytes, bool useSudo)
    {
        string quoted = RemotePathUtils.ShellQuote(device);
        string read;
        if (headHasBytes)
        {
            read = "head -c " + size.ToString(CultureInfo.InvariantCulture) + " " + quoted;
        }
        else
        {
            long block = 65536;
            while (block > 1 && size % block != 0)
            {
                block /= 2;
            }
            read = "dd if=" + quoted + " bs=" + block.ToString(CultureInfo.InvariantCulture) + " count=" + (size / block).ToString(CultureInfo.InvariantCulture) + " 2>/dev/null";
        }
        return WrapForUser(read + " | " + HashProgram(algorithm), useSudo);
    }

    /// <summary>The command line that prints the checksum of a file (the image in RAM).</summary>
    public static string BuildFileHashCommand(string path, string algorithm, bool useSudo)
    {
        return WrapForUser(HashProgram(algorithm) + " " + RemotePathUtils.ShellQuote(path), useSudo);
    }

    /// <summary>The checksum in the output of sha256sum or md5sum ("hash  name"), lower case; null when there is none.</summary>
    public static string ParseHash(IEnumerable<string> lines, string algorithm)
    {
        int length = algorithm == "sha256" ? 64 : 32;
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string first = rawLine?.Trim().Split(' ', '\t').FirstOrDefault();
            if (first != null && first.Length == length && first.All(Uri.IsHexDigit))
            {
                return first.ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary>The memory kept free besides the image: the system must not start to kill programs while it is in RAM.</summary>
    public static long MemoryMargin(long size)
    {
        return Math.Max(8L * 1024 * 1024, size / 4);
    }

    /// <summary>
    /// The RAM file system to put an image of <paramref name="size"/> bytes in (/tmp first, as on OpenWrt), or null with the reason:
    /// there must be room for it in the file system, and the memory left after it must stay above <see cref="MemoryMargin"/>.
    /// </summary>
    public static MtdRamDirectory ChooseRamDirectory(MtdHostInfo info, long size, out string reason)
    {
        reason = null;
        long? usable = info?.UsableMemoryBytes;
        if (!usable.HasValue)
        {
            reason = "the server does not tell how much memory is free (/proc/meminfo)";
            return null;
        }
        long needed = size + MemoryMargin(size);
        if (usable.Value < needed)
        {
            reason = "the server has " + FormatBytes(usable.Value) + " of memory available, and an image of " + FormatBytes(size) + " needs " + FormatBytes(needed) + " (the image and a reserve for the system)";
            return null;
        }
        List<MtdRamDirectory> candidates = (info.RamDirectories ?? new List<MtdRamDirectory>())
            .Where(d => d.Path != "/dev" && !d.Path.StartsWith("/sys", StringComparison.Ordinal) && !d.Path.StartsWith("/proc", StringComparison.Ordinal))
            .OrderBy(d => d.Path == "/tmp" ? 0 : d.Path == "/dev/shm" ? 1 : d.Path.StartsWith("/run", StringComparison.Ordinal) ? 2 : 3)
            .ThenBy(d => d.Path, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            reason = "the server has no file system in RAM (tmpfs or ramfs) to put the image in before it is written";
            return null;
        }
        foreach (MtdRamDirectory directory in candidates)
        {
            if (directory.Type == "ramfs" || directory.FreeBytes.HasValue && directory.FreeBytes.Value >= size + 64 * 1024)
            {
                return directory;
            }
        }
        reason = "no RAM file system of the server has room for " + FormatBytes(size) + " (" + string.Join(", ", candidates.Select(d => d.Path + ": " + (d.FreeBytes.HasValue ? FormatBytes(d.FreeBytes.Value) : "?") + " free")) + ")";
        return null;
    }

    /// <summary>The path of the image in RAM: a new name each time, so that nothing there is replaced.</summary>
    public static string RamImagePath(MtdRamDirectory directory, string unique)
    {
        return RemoteBackup.Combine(directory.Path, "smartty-mtd-" + unique + ".bin");
    }

    /// <summary>
    /// The command line that stores its standard input in <paramref name="path"/> (the image in RAM): only readable by its owner, and
    /// never over an existing file (noclobber).
    /// </summary>
    public static string BuildUploadCommand(string path, bool useSudo)
    {
        return WrapForUser("set -C; umask 077; exec cat > " + RemotePathUtils.ShellQuote(path), useSudo);
    }

    public static string BuildRemoveCommand(string path, bool useSudo)
    {
        return WrapForUser("rm -f " + RemotePathUtils.ShellQuote(path), useSudo);
    }

    /// <summary>
    /// The command line that writes the image at <paramref name="path"/> into the partition with the flash tool of the system, or null
    /// with the reason when there is none for it: mtd (OpenWrt; erases as it writes, skips bad blocks of NAND), flashcp (mtd-utils,
    /// NOR), or flash_erase and nandwrite (mtd-utils, NAND). <paramref name="tool"/> names the one chosen.
    /// </summary>
    public static string BuildWriteCommand(MtdHostInfo info, MtdPartition partition, string path, bool useSudo, out string tool, out string reason)
    {
        tool = null;
        reason = null;
        if (!partition.HasCharDevice)
        {
            reason = "the server has no character device /dev/mtd" + partition.Index.ToString(CultureInfo.InvariantCulture) + " to write with";
            return null;
        }
        string type = partition.Type ?? "";
        if (type == "ubi" || type == "rom" || type == "absent" || type == "ram")
        {
            reason = "a partition of type " + type + " is not written as raw flash";
            return null;
        }
        string device = RemotePathUtils.ShellQuote(partition.Device);
        string image = RemotePathUtils.ShellQuote(path);
        string script;
        if (info.Tools.Contains("mtd"))
        {
            tool = "mtd";
            script = "mtd write " + image + " " + device;
        }
        else if (partition.IsNand)
        {
            if (!info.Tools.Contains("flash_erase") || !info.Tools.Contains("nandwrite"))
            {
                reason = "writing NAND needs mtd (OpenWrt) or flash_erase and nandwrite (mtd-utils), and the server has none of them";
                return null;
            }
            tool = "flash_erase + nandwrite";
            // "flash_erase device 0 0" erases to the end of the partition on purpose, not just as many blocks as the image
            // fills. flash_erase counts the bad blocks of the range it is given (it skips them, but they use up the
            // count), while nandwrite skips a bad block and writes the data on into the next good one. With a count
            // computed from the image size, k bad blocks in that range push the last k blocks of the image past the
            // erased range onto blocks that still hold the old data, and nandwrite does not report it. Erasing the
            // whole partition leaves nandwrite only erased good blocks to write to; CheckRestore warns that the rest
            // of the partition is left empty (0xFF).
            script = "flash_erase " + device + " 0 0 && nandwrite -p " + device + " " + image;
        }
        else
        {
            if (!info.Tools.Contains("flashcp"))
            {
                reason = "writing the flash needs mtd (OpenWrt) or flashcp (mtd-utils), and the server has neither";
                return null;
            }
            tool = "flashcp";
            script = "flashcp " + image + " " + device;
        }
        return WrapForUser(script, useSudo);
    }

    /// <summary>Why writing this partition is especially dangerous; null when it is not.</summary>
    public static string RiskNote(MtdPartition partition)
    {
        string name = (partition?.Name ?? "").ToLowerInvariant();
        if (name.Contains("u-boot") || name.Contains("uboot") || name.Contains("bootloader") || name.Contains("preloader") || name == "boot" || name == "bl2" || name == "fip" || name == "spl" || name == "atf" || name.StartsWith("sbl", StringComparison.Ordinal) || name == "cfe" || name == "redboot" || name == "breed")
        {
            if (name.Contains("env"))
            {
                return "it holds the settings of the boot loader";
            }
            return "it holds the boot loader: a wrong image makes the device unbootable, and only a hardware programmer brings it back";
        }
        if (name == "art" || name == "factory" || name.Contains("caldata") || name.Contains("eeprom") || name == "calibration" || name.Contains("board_data") || name == "bdinfo")
        {
            return "it holds the radio calibration and the MAC addresses of this very unit: an image of another device breaks Wi-Fi for good";
        }
        if (partition?.InUse != null)
        {
            return "it is in use (" + partition.InUse + "): the running system may crash while it is written";
        }
        if (name == "rootfs" || name == "rootfs_data" || name == "firmware" || name == "ubi" || name == "kernel")
        {
            return "the running system is in it: the device may misbehave until it is restarted";
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="part"/> can be written into <paramref name="partition"/>: errors stop it, warnings are asked about.
    /// </summary>
    public static void CheckRestore(MtdImagePart part, MtdPartition partition, MtdHostInfo info, List<string> errors, List<string> warnings)
    {
        if (partition == null)
        {
            errors.Add("no partition is chosen to write it to");
            return;
        }
        if (part.Size > partition.Size)
        {
            errors.Add("the image (" + FormatBytes(part.Size) + ") is bigger than " + partition.Label + " (" + FormatBytes(partition.Size) + ")");
        }
        if (part.Size <= 0)
        {
            errors.Add("the image is empty");
        }
        if (partition.Writable == false)
        {
            errors.Add(partition.Label + " is read-only for the kernel");
        }
        if (info.HashAlgorithm == null)
        {
            errors.Add("the server has neither sha256sum nor md5sum, so the image in RAM and the flash cannot be checked");
        }
        BuildWriteCommand(info, partition, "/tmp/x", false, out string writeTool, out string reason);
        if (reason != null)
        {
            errors.Add(reason);
        }
        if (part.Size < partition.Size && part.Size > 0)
        {
            // flash_erase + nandwrite erases the whole partition (see BuildWriteCommand), mtd and flashcp only what they write.
            warnings.Add("the image is smaller than " + partition.Label + ": only its first " + FormatBytes(part.Size) + " are written and checked"
                + (writeTool == "flash_erase + nandwrite" ? "; the rest of the partition is erased and left empty (0xFF), its old data is lost" : ""));
        }
        if (part.Name != null && !string.Equals(part.Name, partition.Name, StringComparison.Ordinal))
        {
            warnings.Add("the image was read from \"" + part.Name + "\" and is written to " + partition.Label);
        }
        if (part.Name != null && part.Size != partition.Size && string.Equals(part.Name, partition.Name, StringComparison.Ordinal))
        {
            warnings.Add("the partition had another size when the image was made (" + FormatBytes(part.Size) + " then, " + FormatBytes(partition.Size) + " now)");
        }
        string risk = RiskNote(partition);
        if (risk != null)
        {
            warnings.Add(partition.Label + ": " + risk);
        }
    }

    /// <summary>The partition of the server that an image part belongs to: the same name and size, else the same name, else none.</summary>
    public static MtdPartition MatchPartition(MtdImagePart part, IList<MtdPartition> partitions)
    {
        if (part?.Name == null || partitions == null)
        {
            return null;
        }
        return partitions.FirstOrDefault(p => p.Name == part.Name && p.Size == part.Size)
            ?? partitions.FirstOrDefault(p => p.Name == part.Name && p.Index == part.MtdIndex)
            ?? partitions.FirstOrDefault(p => p.Name == part.Name);
    }

    /// <summary>"router-20261102-1030-mtd3-firmware.bin": no character that Windows forbids.</summary>
    public static string ImageFileName(string prefix, MtdPartition partition)
    {
        return prefix + "-mtd" + partition.Index.ToString(CultureInfo.InvariantCulture) + "-" + SafeName(partition.Name) + ".bin";
    }

    /// <summary>"router-20261102-1030": the host and the time, the start of every file of one backup.</summary>
    public static string FilePrefix(string host, DateTime time)
    {
        string name = SafeName(host);
        return (name.Length == 0 ? "flash" : name) + "-" + time.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
    }

    public static string SafeName(string text)
    {
        StringBuilder safe = new StringBuilder();
        foreach (char c in text ?? "")
        {
            safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_' ? c : '_');
        }
        return safe.ToString().Trim('.', '_');
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
