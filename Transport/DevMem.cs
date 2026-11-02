using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SmarTTY.Transport;

/// <summary>The program that reads and writes physical memory on the server.</summary>
public enum DevMemTool
{
    None,

    /// <summary>devmem in the PATH (BusyBox applet link, or a standalone build).</summary>
    DevMem,

    /// <summary>BusyBox has the applet, but no link to it: "busybox devmem".</summary>
    BusyBoxDevMem,

    /// <summary>devmem2 (the classic tool of embedded Linux: b/h/w widths, 32 bits at most).</summary>
    DevMem2
}

/// <summary>The value read at one address, or why it could not be read.</summary>
public sealed class DevMemReading
{
    public ulong Address { get; set; }

    public ulong? Value { get; set; }

    public string Error { get; set; }
}

/// <summary>A region of the physical address space, from /proc/iomem or a built-in map.</summary>
public sealed class MemoryRegion
{
    public ulong Start { get; set; }

    public ulong End { get; set; }

    public string Name { get; set; }

    /// <summary>The nesting in /proc/iomem (0 for the top level).</summary>
    public int Depth { get; set; }

    public ulong Size => End >= Start ? End - Start + 1 : 0;

    public override string ToString()
    {
        return "0x" + Start.ToString("X8", CultureInfo.InvariantCulture) + "-0x" + End.ToString("X8", CultureInfo.InvariantCulture) + "  " + new string(' ', Depth * 2) + Name;
    }
}

/// <summary>
/// Tools > Memory (devmem): the commands that read and write physical memory with devmem (BusyBox) or devmem2, the reading of
/// their output, the forms a value is shown and typed in (hex, decimal, signed, binary, ASCII, float) and the bit operations.
/// One SSH command reads a whole list of addresses: a loop of devmem calls whose outputs are framed by markers, so that an error
/// (a bus error, "Permission denied") belongs to its address. Pure logic without WPF or SSH, tested by tests/DevMemTests.
/// </summary>
public static class DevMem
{
    public const string ProbeMarker = "SMARTTY_DEVMEM=";

    /// <summary>
    /// The part of the host probe that finds the tool: devmem in the PATH, the devmem applet of BusyBox without a link to it, or
    /// devmem2; "none" when the host has none of them (the Tools item is then not shown).
    /// </summary>
    public const string ProbeCommand =
        "if command -v devmem >/dev/null 2>&1; then echo " + ProbeMarker + "devmem; " +
        "elif command -v busybox >/dev/null 2>&1 && busybox --list 2>/dev/null | grep -qx devmem; then echo " + ProbeMarker + "busybox; " +
        "elif command -v devmem2 >/dev/null 2>&1; then echo " + ProbeMarker + "devmem2; " +
        "else echo " + ProbeMarker + "none; fi";

    /// <summary>The tool from the output of <see cref="ProbeCommand"/>; null when the output has no answer.</summary>
    public static DevMemTool? FromProbe(IEnumerable<string> lines)
    {
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = rawLine?.Trim();
            if (line == null || !line.StartsWith(ProbeMarker, StringComparison.Ordinal))
            {
                continue;
            }
            switch (line.Substring(ProbeMarker.Length))
            {
                case "devmem":
                    return DevMemTool.DevMem;
                case "busybox":
                    return DevMemTool.BusyBoxDevMem;
                case "devmem2":
                    return DevMemTool.DevMem2;
                case "none":
                    return DevMemTool.None;
            }
        }
        return null;
    }

    public static string ToolName(DevMemTool tool)
    {
        switch (tool)
        {
            case DevMemTool.DevMem:
                return "devmem";
            case DevMemTool.BusyBoxDevMem:
                return "busybox devmem";
            case DevMemTool.DevMem2:
                return "devmem2";
            default:
                return null;
        }
    }

    /// <summary>The access widths in bits: devmem knows 8, 16, 32 and 64, devmem2 only b, h and w.</summary>
    public static int[] Widths(DevMemTool tool)
    {
        return tool == DevMemTool.DevMem2 ? new[] { 8, 16, 32 } : new[] { 8, 16, 32, 64 };
    }

    /// <summary>The width argument of the tool: the number of bits for devmem, b/h/w for devmem2.</summary>
    public static string WidthArgument(DevMemTool tool, int width)
    {
        if (!Widths(tool).Contains(width))
        {
            throw new ArgumentException(ToolName(tool) + " cannot access " + width + " bits at once.", nameof(width));
        }
        if (tool == DevMemTool.DevMem2)
        {
            return width == 8 ? "b" : width == 16 ? "h" : "w";
        }
        return width.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"0x43C00000".</summary>
    public static string AddressText(ulong address)
    {
        return "0x" + address.ToString(address > uint.MaxValue ? "X16" : "X8", CultureInfo.InvariantCulture);
    }

    private const string BeginMarker = "SMARTTY_DM_BEGIN ";

    private const string EndMarker = "SMARTTY_DM_END";

    /// <summary>
    /// The command line that reads every address with one call of the tool each, inside one shell: before each call the address
    /// is printed with a marker and after it the exit status, so that whatever the tool (or the shell, on a bus error) prints
    /// belongs to that address. The order of the output is the order of <paramref name="addresses"/>.
    /// </summary>
    public static string BuildReadCommand(DevMemTool tool, IEnumerable<ulong> addresses, int width, bool useSudo)
    {
        string program = ToolName(tool) ?? throw new InvalidOperationException("The server has neither devmem nor devmem2.");
        string widthArgument = WidthArgument(tool, width);
        StringBuilder script = new StringBuilder();
        foreach (ulong address in addresses)
        {
            string text = AddressText(address);
            script.Append("echo '").Append(BeginMarker).Append(text).Append("'; ")
                .Append(program).Append(' ').Append(text).Append(' ').Append(widthArgument).Append(" 2>&1; ")
                .Append("echo \"").Append(EndMarker).Append(" $?\"; ");
        }
        if (script.Length == 0)
        {
            throw new ArgumentException("No address to read.", nameof(addresses));
        }
        return Wrap(script.ToString(), useSudo);
    }

    /// <summary>
    /// The command line that writes <paramref name="value"/> at the address and then reads it back with a second call, framed like
    /// <see cref="BuildReadCommand"/>, so that the window shows what the register holds after the write (a read-only bit, a bit
    /// that clears itself, a register that reads something else than what is written).
    /// </summary>
    public static string BuildWriteCommand(DevMemTool tool, ulong address, int width, ulong value, bool useSudo)
    {
        string program = ToolName(tool) ?? throw new InvalidOperationException("The server has neither devmem nor devmem2.");
        CheckFits(value, width);
        string text = AddressText(address);
        string widthArgument = WidthArgument(tool, width);
        string script = "echo 'SMARTTY_DM_WRITE'; " + program + " " + text + " " + widthArgument + " " + FormatHex(value, width) + " 2>&1; echo \"SMARTTY_DM_WRITTEN $?\"; " +
            "echo '" + BeginMarker + text + "'; " + program + " " + text + " " + widthArgument + " 2>&1; echo \"" + EndMarker + " $?\"";
        return Wrap(script, useSudo);
    }

    private static string Wrap(string script, bool useSudo)
    {
        string command = SmarTTY.Utilities.PosixShell.Wrap(script, addAdminDirectoriesToPath: true);
        return useSudo ? "sudo -n " + command : command;
    }

    /// <summary>The readings in the output of <see cref="BuildReadCommand"/> (or the read-back of <see cref="BuildWriteCommand"/>).</summary>
    public static List<DevMemReading> ParseReadOutput(IEnumerable<string> lines)
    {
        List<DevMemReading> readings = new List<DevMemReading>();
        DevMemReading current = null;
        List<string> body = new List<string>();
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = (rawLine ?? "").TrimEnd('\r');
            if (line.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                current = new DevMemReading();
                body.Clear();
                if (!TryParseAddress(line.Substring(BeginMarker.Length), out ulong address, out _))
                {
                    current = null;
                    continue;
                }
                current.Address = address;
                continue;
            }
            if (line.StartsWith(EndMarker, StringComparison.Ordinal) && current != null)
            {
                string status = line.Substring(EndMarker.Length).Trim();
                ulong? value = ParseToolValue(body);
                if (status == "0" && value.HasValue)
                {
                    current.Value = value;
                }
                else
                {
                    current.Error = DescribeError(body, status);
                }
                readings.Add(current);
                current = null;
                continue;
            }
            if (current != null)
            {
                body.Add(line);
            }
        }
        return readings;
    }

    /// <summary>The exit status of the write itself in the output of <see cref="BuildWriteCommand"/>, with what the tool printed.</summary>
    public static bool ParseWriteStatus(IEnumerable<string> lines, out string message)
    {
        message = null;
        bool inWrite = false;
        List<string> body = new List<string>();
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = (rawLine ?? "").TrimEnd('\r');
            if (line == "SMARTTY_DM_WRITE")
            {
                inWrite = true;
                continue;
            }
            if (inWrite && line.StartsWith("SMARTTY_DM_WRITTEN", StringComparison.Ordinal))
            {
                string status = line.Substring("SMARTTY_DM_WRITTEN".Length).Trim();
                if (status == "0")
                {
                    return true;
                }
                message = DescribeError(body, status);
                return false;
            }
            if (inWrite)
            {
                body.Add(line);
            }
        }
        message = "the server did not run the write";
        return false;
    }

    /// <summary>
    /// The value in what the tool printed: BusyBox devmem prints only "0x0000ABCD"; devmem2 prints a few lines and then
    /// "Read at address  0x43C00000 (0xb6f5e000): 0x0000ABCD" (older versions: "Value at address ...").
    /// </summary>
    public static ulong? ParseToolValue(IEnumerable<string> lines)
    {
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && line.IndexOf(' ') < 0)
            {
                if (ulong.TryParse(line.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
                {
                    return value;
                }
            }
            if ((line.StartsWith("Read at address", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Value at address", StringComparison.OrdinalIgnoreCase)) && line.LastIndexOf(':') > 0)
            {
                string tail = line.Substring(line.LastIndexOf(':') + 1).Trim();
                if (tail.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(tail.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
                {
                    return value;
                }
            }
        }
        return null;
    }

    private static string DescribeError(List<string> body, string status)
    {
        string text = string.Join(" ", body.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("/dev/mem opened", StringComparison.Ordinal) && !l.StartsWith("Memory mapped at", StringComparison.Ordinal)));
        if (status == "135" || status == "138" || text.IndexOf("Bus error", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "bus error: nothing answers at this address";
        }
        if (status == "139" || text.IndexOf("Segmentation fault", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "segmentation fault: the address cannot be mapped";
        }
        if (text.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Operation not permitted", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "permission denied: /dev/mem needs root (or the kernel restricts it, CONFIG_STRICT_DEVMEM)";
        }
        if (text.Length == 0)
        {
            return "no value (exit status " + (status.Length > 0 ? status : "unknown") + ")";
        }
        return text.Length > 160 ? text.Substring(0, 157) + "..." : text;
    }

    /// <summary>Reads an address: hexadecimal with or without 0x ("43C00000", "0x43c0_0000"), or decimal with a "#" in front ("#1024").</summary>
    public static bool TryParseAddress(string text, out ulong address, out string error)
    {
        address = 0;
        error = null;
        string clean = (text ?? "").Trim().Replace("_", "").Replace(" ", "").Replace("'", "");
        if (clean.Length == 0)
        {
            error = "Enter an address.";
            return false;
        }
        if (clean.StartsWith("#", StringComparison.Ordinal))
        {
            if (ulong.TryParse(clean.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out address))
            {
                return true;
            }
            error = "\"" + text.Trim() + "\" is not a decimal address.";
            return false;
        }
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(2);
        }
        if (clean.Length == 0 || clean.Length > 16 || !ulong.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address))
        {
            error = "\"" + text.Trim() + "\" is not a hexadecimal address.";
            return false;
        }
        return true;
    }

    /// <summary>Why the address cannot be accessed with the width; null when it can (aligned to the width).</summary>
    public static string CheckAlignment(ulong address, int width)
    {
        ulong bytes = (ulong)(width / 8);
        return bytes > 1 && address % bytes != 0
            ? AddressText(address) + " is not aligned to " + width + " bits (a multiple of " + bytes + " bytes): ARM raises an alignment fault for such an access to device memory."
            : null;
    }

    /// <summary>The largest value of the width.</summary>
    public static ulong Mask(int width)
    {
        return width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
    }

    private static void CheckFits(ulong value, int width)
    {
        if ((value & ~Mask(width)) != 0)
        {
            throw new ArgumentException("0x" + value.ToString("X", CultureInfo.InvariantCulture) + " does not fit into " + width + " bits.", nameof(value));
        }
    }

    /// <summary>
    /// Reads a value as it is typed: 0x hex, 0b binary, 0o octal, a decimal number, or a negative decimal number (stored as its
    /// two's complement in the width). '_' and spaces may group the digits. Null with the reason when it is not a value or does not
    /// fit into <paramref name="width"/> bits.
    /// </summary>
    public static ulong? ParseValue(string text, int width, out string error)
    {
        error = null;
        string clean = (text ?? "").Trim().Replace("_", "").Replace(" ", "").Replace("'", "");
        if (clean.Length == 0)
        {
            error = "Enter a value.";
            return null;
        }
        bool negative = clean.StartsWith("-", StringComparison.Ordinal);
        if (negative)
        {
            clean = clean.Substring(1);
        }
        ulong value = 0;
        bool ok;
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            ok = clean.Length > 2 && clean.Length <= 18 && ulong.TryParse(clean.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        else if (clean.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            ok = TryParseBase(clean.Substring(2), 2, out value);
        }
        else if (clean.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            ok = TryParseBase(clean.Substring(2), 8, out value);
        }
        else
        {
            ok = ulong.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        if (!ok)
        {
            error = "\"" + text.Trim() + "\" is not a number (use 0x1F, 0b1010, 31 or -1).";
            return null;
        }
        if (negative)
        {
            ulong limit = width >= 64 ? 1UL << 63 : 1UL << (width - 1);
            if (value > limit)
            {
                error = "-" + value + " does not fit into " + width + " signed bits.";
                return null;
            }
            value = (0UL - value) & Mask(width);
        }
        if ((value & ~Mask(width)) != 0)
        {
            error = "The value does not fit into " + width + " bits (at most 0x" + Mask(width).ToString("X", CultureInfo.InvariantCulture) + ").";
            return null;
        }
        return value;
    }

    private static bool TryParseBase(string digits, int radix, out ulong value)
    {
        value = 0;
        if (digits.Length == 0)
        {
            return false;
        }
        foreach (char c in digits)
        {
            int digit = c - '0';
            if (digit < 0 || digit >= radix)
            {
                return false;
            }
            if (value > (ulong.MaxValue - (ulong)digit) / (ulong)radix)
            {
                return false;
            }
            value = value * (ulong)radix + (ulong)digit;
        }
        return true;
    }

    public static string FormatHex(ulong value, int width)
    {
        return "0x" + (value & Mask(width)).ToString("X" + (width / 4).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    public static string FormatUnsigned(ulong value, int width)
    {
        return (value & Mask(width)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The value as a two's complement number of the width.</summary>
    public static long ToSigned(ulong value, int width)
    {
        value &= Mask(width);
        if (width >= 64)
        {
            return unchecked((long)value);
        }
        ulong sign = 1UL << (width - 1);
        return (value & sign) != 0 ? (long)value - (long)(1UL << width) : (long)value;
    }

    public static string FormatSigned(ulong value, int width)
    {
        return ToSigned(value, width).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"0000 0000 0000 0000 1010 1011 1100 1101": the bits from the most significant one, in groups of four.</summary>
    public static string FormatBinary(ulong value, int width)
    {
        StringBuilder text = new StringBuilder(width + width / 4);
        for (int bit = width - 1; bit >= 0; bit--)
        {
            text.Append(((value >> bit) & 1) != 0 ? '1' : '0');
            if (bit % 4 == 0 && bit > 0)
            {
                text.Append(' ');
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// The bytes of the value in the order of their addresses (little-endian, as Zynq and other ARM systems store them): a printable
    /// ASCII character each, '.' for the others.
    /// </summary>
    public static string FormatAscii(ulong value, int width)
    {
        StringBuilder text = new StringBuilder(width / 8);
        for (int i = 0; i < width / 8; i++)
        {
            byte b = (byte)(value >> (8 * i));
            text.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        return text.ToString();
    }

    /// <summary>The value as an IEEE 754 float (32 bits) or double (64 bits); empty for the other widths.</summary>
    public static string FormatFloat(ulong value, int width)
    {
        if (width == 32)
        {
            float f = BitConverter.ToSingle(BitConverter.GetBytes((uint)value), 0);
            return f.ToString("R", CultureInfo.InvariantCulture);
        }
        if (width == 64)
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)value)).ToString("R", CultureInfo.InvariantCulture);
        }
        return "";
    }

    public static bool GetBit(ulong value, int bit)
    {
        return ((value >> bit) & 1) != 0;
    }

    public static ulong SetBit(ulong value, int bit, bool on)
    {
        return on ? value | (1UL << bit) : value & ~(1UL << bit);
    }

    /// <summary>The mask of bits <paramref name="lsb"/> to <paramref name="msb"/> (inclusive).</summary>
    public static ulong FieldMask(int msb, int lsb)
    {
        if (lsb < 0 || msb < lsb || msb > 63)
        {
            throw new ArgumentException("A bit field is [msb:lsb] with 63 >= msb >= lsb >= 0.");
        }
        int count = msb - lsb + 1;
        return (count >= 64 ? ulong.MaxValue : (1UL << count) - 1) << lsb;
    }

    public static ulong GetField(ulong value, int msb, int lsb)
    {
        return (value & FieldMask(msb, lsb)) >> lsb;
    }

    /// <summary>The value with bits [msb:lsb] replaced by <paramref name="field"/>; throws when the field does not fit.</summary>
    public static ulong SetField(ulong value, int msb, int lsb, ulong field)
    {
        ulong mask = FieldMask(msb, lsb);
        if (((field << lsb) & ~mask) != 0 || (lsb > 0 && field >> (64 - lsb) != 0))
        {
            throw new ArgumentException("0x" + field.ToString("X", CultureInfo.InvariantCulture) + " does not fit into the " + (msb - lsb + 1) + " bits [" + msb + ":" + lsb + "].");
        }
        return (value & ~mask) | (field << lsb);
    }

    /// <summary>Reads "[7:4]", "7:4", "7..4" or "5" (one bit) as a bit field.</summary>
    public static bool TryParseField(string text, out int msb, out int lsb)
    {
        msb = lsb = -1;
        string clean = (text ?? "").Trim().TrimStart('[').TrimEnd(']').Replace("..", ":").Replace(" ", "");
        string[] parts = clean.Split(':');
        if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out msb))
        {
            lsb = msb;
        }
        else if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out msb) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out lsb))
        {
            return false;
        }
        if (msb < lsb)
        {
            (msb, lsb) = (lsb, msb);
        }
        return lsb >= 0 && msb <= 63;
    }

    /// <summary>Prints the uid of the user and whether sudo works without a password (devmem needs root for /dev/mem).</summary>
    public const string UserCommand =
        "echo SMARTTY_UID=$(id -u 2>/dev/null); " +
        "if [ \"$(id -u 2>/dev/null)\" != 0 ] && command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then echo SMARTTY_SUDO=yes; fi";

    /// <summary>Reads the output of <see cref="UserCommand"/>: whether the user is root (null when unknown) and may use sudo -n.</summary>
    public static bool? ParseUser(IEnumerable<string> lines, out bool canSudo)
    {
        canSudo = false;
        bool? isRoot = null;
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = rawLine?.Trim() ?? "";
            if (line.StartsWith("SMARTTY_UID=", StringComparison.Ordinal) && line.Length > 12 && line.Substring(12).All(char.IsDigit))
            {
                isRoot = line.Substring(12) == "0";
            }
            else if (line == "SMARTTY_SUDO=yes")
            {
                canSudo = true;
            }
        }
        return isRoot;
    }

    /// <summary>The command line that prints /proc/iomem (the addresses are zero for a user other than root).</summary>
    public static string BuildIomemCommand(bool useSudo)
    {
        return Wrap("cat /proc/iomem", useSudo);
    }

    /// <summary>The regions of /proc/iomem ("  43c00000-43c0ffff : /amba_pl/axi_gpio@43c00000"); the indentation is the nesting.</summary>
    public static List<MemoryRegion> ParseIomem(IEnumerable<string> lines)
    {
        List<MemoryRegion> regions = new List<MemoryRegion>();
        foreach (string rawLine in lines ?? Enumerable.Empty<string>())
        {
            string line = (rawLine ?? "").TrimEnd('\r');
            int colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }
            string range = line.Substring(0, colon);
            int indent = range.Length - range.TrimStart().Length;
            string[] bounds = range.Trim().Split('-');
            if (bounds.Length != 2
                || !ulong.TryParse(bounds[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong start)
                || !ulong.TryParse(bounds[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong end)
                || end < start)
            {
                continue;
            }
            regions.Add(new MemoryRegion { Start = start, End = end, Name = line.Substring(colon + 3).Trim(), Depth = indent / 2 });
        }
        return regions;
    }

    /// <summary>The regions of /proc/iomem are all zero: the kernel hides the addresses from a user other than root.</summary>
    public static bool IomemHidden(IList<MemoryRegion> regions)
    {
        return regions.Count > 0 && regions.All(r => r.Start == 0 && r.End == 0);
    }

    /// <summary>
    /// The address map of the Zynq-7000 (Xilinx UG585, appendix B), for the regions the window offers: the PL behind the general
    /// purpose AXI ports and the registers of the processing system that the debugging of drivers and of the PL needs most.
    /// </summary>
    public static readonly MemoryRegion[] Zynq7000Map =
    {
        new MemoryRegion { Start = 0x40000000, End = 0x7FFFFFFF, Name = "PL: M_AXI_GP0 (AXI peripherals of the PL, e.g. 0x43C00000)" },
        new MemoryRegion { Start = 0x80000000, End = 0xBFFFFFFF, Name = "PL: M_AXI_GP1" },
        new MemoryRegion { Start = 0xE0000000, End = 0xE0000FFF, Name = "UART0" },
        new MemoryRegion { Start = 0xE0001000, End = 0xE0001FFF, Name = "UART1" },
        new MemoryRegion { Start = 0xE0002000, End = 0xE0002FFF, Name = "USB0" },
        new MemoryRegion { Start = 0xE0004000, End = 0xE0004FFF, Name = "I2C0" },
        new MemoryRegion { Start = 0xE0005000, End = 0xE0005FFF, Name = "I2C1" },
        new MemoryRegion { Start = 0xE0006000, End = 0xE0006FFF, Name = "SPI0" },
        new MemoryRegion { Start = 0xE0007000, End = 0xE0007FFF, Name = "SPI1" },
        new MemoryRegion { Start = 0xE000A000, End = 0xE000AFFF, Name = "GPIO (MIO and EMIO)" },
        new MemoryRegion { Start = 0xE000B000, End = 0xE000BFFF, Name = "GEM0 (Ethernet 0)" },
        new MemoryRegion { Start = 0xE0100000, End = 0xE0100FFF, Name = "SDIO0" },
        new MemoryRegion { Start = 0xF8000000, End = 0xF8000BFF, Name = "SLCR (clocks, resets, MIO pins, FPGA_RST_CTRL at 0x240, LVL_SHFTR_EN at 0x900)" },
        new MemoryRegion { Start = 0xF8001000, End = 0xF8001FFF, Name = "TTC0 (triple timer counter)" },
        new MemoryRegion { Start = 0xF8003000, End = 0xF8003FFF, Name = "DMAC (secure)" },
        new MemoryRegion { Start = 0xF8005000, End = 0xF8005FFF, Name = "SWDT (watchdog)" },
        new MemoryRegion { Start = 0xF8006000, End = 0xF8006FFF, Name = "DDRC (DDR controller)" },
        new MemoryRegion { Start = 0xF8007000, End = 0xF80070FF, Name = "DEVCFG (PL configuration: STATUS at 0x14, INT_STS at 0x0C)" },
        new MemoryRegion { Start = 0xF8007100, End = 0xF800711F, Name = "XADC interface" },
        new MemoryRegion { Start = 0xF8F00000, End = 0xF8F00FFF, Name = "SCU, GIC CPU interface, private timers" },
        new MemoryRegion { Start = 0xF8F01000, End = 0xF8F01FFF, Name = "GIC distributor" },
        new MemoryRegion { Start = 0xFFFC0000, End = 0xFFFFFFFF, Name = "OCM (high, 256 KB)" }
    };
}
