using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SmarTTY.Transport;

// Regression harness for Tools > Memory (devmem) (Transport/DevMem.cs, Transport/MemoryRows.cs): the probe for devmem, busybox devmem
// and devmem2, the values typed and shown in every form, bits and bit fields, the reading of the tool output (BusyBox and devmem2
// formats, bus errors, permission errors), /proc/iomem, the rows with their change highlight, the saved register lists, and the read
// and write commands run for real by /bin/sh and BusyBox sh against stand-ins of the tools (fake-devmem.py) on a file of "memory".
// Run with: dotnet run --project tests/DevMemTests
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

    private static int Main()
    {
        _work = Path.Combine(Path.GetTempPath(), "smartty-devmem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        try
        {
            ProbeTests();
            ValueTests();
            BitTests();
            OutputTests();
            IomemTests();
            RowTests();
            WorkspaceTests();
            string python = new[] { "/usr/bin/python3", "/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists);
            if (OperatingSystem.IsWindows() || python == null)
            {
                Console.WriteLine("SKIP: the shell commands are not run (no POSIX shell or no python3 for the stand-in tools)");
            }
            else
            {
                string busybox = new[] { "/usr/bin/busybox", "/bin/busybox" }.FirstOrDefault(File.Exists);
                ProbeCommandTests(busybox);
                List<string> shells = new List<string> { null };
                if (busybox != null)
                {
                    shells.Add(busybox);
                }
                else
                {
                    Console.WriteLine("SKIP: busybox is not installed, the commands are not run with BusyBox sh");
                }
                foreach (string shell in shells)
                {
                    foreach (DevMemTool tool in new[] { DevMemTool.DevMem, DevMemTool.BusyBoxDevMem, DevMemTool.DevMem2 })
                    {
                        ShellTests(shell, tool);
                    }
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

    private static void ProbeTests()
    {
        Equal(DevMem.FromProbe(new[] { "SMARTTY_TAR=yes", "SMARTTY_DEVMEM=devmem" }), (DevMemTool?)DevMemTool.DevMem, "probe: devmem");
        Equal(DevMem.FromProbe(new[] { "SMARTTY_DEVMEM=busybox" }), (DevMemTool?)DevMemTool.BusyBoxDevMem, "probe: the applet of busybox");
        Equal(DevMem.FromProbe(new[] { " SMARTTY_DEVMEM=devmem2\r" }), (DevMemTool?)DevMemTool.DevMem2, "probe: devmem2");
        Equal(DevMem.FromProbe(new[] { "SMARTTY_DEVMEM=none" }), (DevMemTool?)DevMemTool.None, "probe: none");
        Equal(DevMem.FromProbe(new[] { "garbage" }), (DevMemTool?)null, "probe: no answer");
        Check(DevMem.Widths(DevMemTool.DevMem).SequenceEqual(new[] { 8, 16, 32, 64 }) && DevMem.Widths(DevMemTool.DevMem2).SequenceEqual(new[] { 8, 16, 32 }), "widths: devmem has 64 bits, devmem2 does not");
        Check(DevMem.WidthArgument(DevMemTool.DevMem2, 8) == "b" && DevMem.WidthArgument(DevMemTool.DevMem2, 16) == "h" && DevMem.WidthArgument(DevMemTool.DevMem2, 32) == "w" && DevMem.WidthArgument(DevMemTool.BusyBoxDevMem, 16) == "16", "width arguments");
        bool threw = false;
        try
        {
            DevMem.WidthArgument(DevMemTool.DevMem2, 64);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "devmem2 refuses 64 bits");
        Equal(DevMem.ParseUser(new[] { "SMARTTY_UID=1000", "SMARTTY_SUDO=yes" }, out bool canSudo), (bool?)false, "user: not root");
        Check(canSudo, "user: passwordless sudo");
        Equal(DevMem.ParseUser(new[] { "SMARTTY_UID=0" }, out canSudo), (bool?)true, "user: root");
        Equal(DevMem.ParseUser(new[] { "SMARTTY_UID=" }, out canSudo), (bool?)null, "user: unknown");
    }

    private static void ValueTests()
    {
        string error;
        Equal(DevMem.ParseValue("0x1F", 32, out error), (ulong?)31, "value: hex");
        Equal(DevMem.ParseValue("0X8000_0000", 32, out error), (ulong?)0x80000000, "value: hex with a separator");
        Equal(DevMem.ParseValue("0b1010 0101", 8, out error), (ulong?)0xA5, "value: binary with a space");
        Equal(DevMem.ParseValue("0o17", 8, out error), (ulong?)15, "value: octal");
        Equal(DevMem.ParseValue("4294967295", 32, out error), (ulong?)uint.MaxValue, "value: decimal");
        Equal(DevMem.ParseValue("-1", 16, out error), (ulong?)0xFFFF, "value: -1 is all ones");
        Equal(DevMem.ParseValue("-128", 8, out error), (ulong?)0x80, "value: the smallest signed byte");
        Check(DevMem.ParseValue("-129", 8, out error) == null && error.Contains("signed"), "value: below the signed range");
        Check(DevMem.ParseValue("0x100", 8, out error) == null && error.Contains("fit"), "value: too big for the width");
        Check(DevMem.ParseValue("0b2", 8, out error) == null && DevMem.ParseValue("abc", 32, out error) == null && DevMem.ParseValue("", 32, out error) == null, "value: not a number");
        Equal(DevMem.ParseValue("0xFFFFFFFFFFFFFFFF", 64, out error), (ulong?)ulong.MaxValue, "value: 64 bits");
        Equal(DevMem.ParseValue("-9223372036854775808", 64, out error), (ulong?)0x8000000000000000, "value: the smallest 64-bit number");

        Equal(DevMem.FormatHex(0xAB, 32), "0x000000AB", "hex: padded to the width");
        Equal(DevMem.FormatHex(0x1FF, 8), "0xFF", "hex: masked to the width");
        Equal(DevMem.FormatUnsigned(0xFFFFFFFF, 32), "4294967295", "unsigned");
        Equal(DevMem.FormatSigned(0xFFFFFFFF, 32), "-1", "signed: -1");
        Equal(DevMem.FormatSigned(0x7F, 8), "127", "signed: the largest byte");
        Equal(DevMem.FormatSigned(0x8000000000000000, 64), "-9223372036854775808", "signed: 64 bits");
        Equal(DevMem.FormatBinary(0xA5, 8), "1010 0101", "binary: grouped by four");
        Equal(DevMem.FormatBinary(1, 16), "0000 0000 0000 0001", "binary: 16 bits");
        Equal(DevMem.FormatAscii(0x44434241, 32), "ABCD", "ascii: little-endian order of the bytes");
        Equal(DevMem.FormatAscii(0x00410A7F, 32), "..A.", "ascii: unprintable bytes are dots");
        Equal(DevMem.FormatFloat(0x3F800000, 32), "1", "float: 1.0");
        Equal(DevMem.FormatFloat(0x4000000000000000, 64), "2", "double: 2.0");
        Equal(DevMem.FormatFloat(5, 16), "", "float: not for 16 bits");

        ulong address;
        Check(DevMem.TryParseAddress("43C00000", out address, out error) && address == 0x43C00000, "address: hex without 0x");
        Check(DevMem.TryParseAddress(" 0xF800_0240 ", out address, out error) && address == 0xF8000240, "address: 0x and a separator");
        Check(DevMem.TryParseAddress("#1024", out address, out error) && address == 1024, "address: decimal with #");
        Check(!DevMem.TryParseAddress("0xZZ", out address, out error) && !DevMem.TryParseAddress("", out address, out error) && !DevMem.TryParseAddress("0x12345678901234567", out address, out error), "address: invalid");
        Check(DevMem.CheckAlignment(0x43C00002, 32) != null && DevMem.CheckAlignment(0x43C00002, 16) == null && DevMem.CheckAlignment(0x43C00003, 8) == null && DevMem.CheckAlignment(0x43C00004, 64) != null, "address: alignment to the width");
        Equal(DevMem.AddressText(0xF8000000), "0xF8000000", "address text: 32 bits");
        Equal(DevMem.AddressText(0x1_0000_0000), "0x0000000100000000", "address text: above 4 GB");
    }

    private static void BitTests()
    {
        Check(DevMem.GetBit(0x80, 7) && !DevMem.GetBit(0x80, 6), "bit: get");
        Equal(DevMem.SetBit(0, 31, true), 0x80000000UL, "bit: set");
        Equal(DevMem.SetBit(0xFF, 0, false), 0xFEUL, "bit: clear");
        Equal(DevMem.FieldMask(7, 4), 0xF0UL, "field: mask");
        Equal(DevMem.FieldMask(63, 0), ulong.MaxValue, "field: all 64 bits");
        Equal(DevMem.GetField(0xABCD, 11, 8), 0xBUL, "field: get");
        Equal(DevMem.SetField(0xABCD, 11, 8, 0x5), 0xA5CDUL, "field: set");
        Equal(DevMem.SetField(0, 63, 60, 0xF), 0xF000000000000000UL, "field: the top bits");
        bool threw = false;
        try
        {
            DevMem.SetField(0, 3, 0, 0x10);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "field: a value too big for the field is refused");
        Check(DevMem.TryParseField("[7:4]", out int msb, out int lsb) && msb == 7 && lsb == 4, "field: [7:4]");
        Check(DevMem.TryParseField("4..7", out msb, out lsb) && msb == 7 && lsb == 4, "field: 4..7 is turned round");
        Check(DevMem.TryParseField("5", out msb, out lsb) && msb == 5 && lsb == 5, "field: one bit");
        Check(!DevMem.TryParseField("64:0", out msb, out lsb) && !DevMem.TryParseField("a:b", out msb, out lsb), "field: invalid");
    }

    private static void OutputTests()
    {
        List<DevMemReading> readings = DevMem.ParseReadOutput(new[]
        {
            "SMARTTY_DM_BEGIN 0x43C00000", "0x0000ABCD", "SMARTTY_DM_END 0",
            "SMARTTY_DM_BEGIN 0x43C00004", "/dev/mem opened.", "Memory mapped at address 0xb6f5e000.", "Read at address  0x43C00004 (0xb6f5e004): 0x00000012", "SMARTTY_DM_END 0",
            "SMARTTY_DM_BEGIN 0x43C00008", "Value at address 0x43C00008 (0xb6f5e008): 0x7", "SMARTTY_DM_END 0",
            "SMARTTY_DM_BEGIN 0x43C0000C", "sh: line 1: 1234 Bus error devmem 0x43C0000C 32", "SMARTTY_DM_END 135",
            "SMARTTY_DM_BEGIN 0x43C00010", "devmem: can't open '/dev/mem': Permission denied", "SMARTTY_DM_END 1",
            "SMARTTY_DM_BEGIN 0x43C00014", "SMARTTY_DM_END 139",
            "SMARTTY_DM_BEGIN 0x43C00018", "weird", "SMARTTY_DM_END 1"
        });
        Equal(readings.Count, 7, "output: every address");
        Check(readings[0].Address == 0x43C00000 && readings[0].Value == 0xABCD, "output: the BusyBox form");
        Check(readings[1].Value == 0x12, "output: the devmem2 form (Read at address)");
        Check(readings[2].Value == 7, "output: the old devmem2 form (Value at address)");
        Check(readings[3].Value == null && readings[3].Error.StartsWith("bus error"), "output: a bus error");
        Check(readings[4].Error.StartsWith("permission denied"), "output: no permission");
        Check(readings[5].Error.StartsWith("segmentation fault"), "output: a segmentation fault");
        Check(readings[6].Error == "weird", "output: another message as it is");
        Check(DevMem.ParseWriteStatus(new[] { "SMARTTY_DM_WRITE", "SMARTTY_DM_WRITTEN 0" }, out _), "write status: done");
        Check(!DevMem.ParseWriteStatus(new[] { "SMARTTY_DM_WRITE", "devmem: can't open '/dev/mem': Permission denied", "SMARTTY_DM_WRITTEN 1" }, out string message) && message.StartsWith("permission denied"), "write status: refused");
        Check(!DevMem.ParseWriteStatus(new string[0], out message) && message.Contains("did not run"), "write status: not run");

        string command = DevMem.BuildReadCommand(DevMemTool.DevMem2, new ulong[] { 0x43C00000 }, 16, useSudo: true);
        Check(command.StartsWith("sudo -n /bin/sh -c ") && command.Contains("devmem2 0x43C00000 h"), "read command: devmem2, 16 bits, sudo");
        Check(DevMem.BuildWriteCommand(DevMemTool.BusyBoxDevMem, 0x43C00000, 8, 0x5A, false).Contains("busybox devmem 0x43C00000 8 0x5A"), "write command: busybox devmem with the hex value");
        bool threw = false;
        try
        {
            DevMem.BuildWriteCommand(DevMemTool.DevMem, 0, 8, 0x100, false);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "write command: a value too big for the width is refused");
    }

    private static void IomemTests()
    {
        List<MemoryRegion> regions = DevMem.ParseIomem(new[]
        {
            "00000000-1fffffff : System RAM",
            "  00008000-008fffff : Kernel code",
            "41200000-4120ffff : /amba_pl/gpio@41200000",
            "43c00000-43c0ffff : /amba_pl/my_ip@43c00000",
            "e000a000-e000afff : e000a000.gpio gpio@e000a000",
            "garbage"
        });
        Check(regions.Count == 5 && regions[1].Depth == 1 && regions[1].Name == "Kernel code", "iomem: the regions and their nesting");
        Check(regions[3].Start == 0x43C00000 && regions[3].End == 0x43C0FFFF && regions[3].Size == 0x10000 && regions[3].Name == "/amba_pl/my_ip@43c00000", "iomem: a PL peripheral");
        Check(DevMem.IomemHidden(DevMem.ParseIomem(new[] { "00000000-00000000 : System RAM", "00000000-00000000 : /amba_pl/x" })), "iomem: hidden addresses (not root)");
        Check(!DevMem.IomemHidden(regions), "iomem: real addresses");
        Check(DevMem.Zynq7000Map.Any(r => r.Start == 0xF8000000 && r.Name.StartsWith("SLCR")) && DevMem.Zynq7000Map.Any(r => r.Start == 0x40000000), "Zynq-7000 map: SLCR and the PL");
    }

    private static void RowTests()
    {
        DateTime t0 = new DateTime(2026, 11, 2, 10, 0, 0);
        MemoryRow row = new MemoryRow(0x43C00004, 32) { BaseAddress = 0x43C00000 };
        Equal(row.OffsetText, "+0x4", "row: offset from the first address");
        Check(!row.Apply(new DevMemReading { Value = 0x10 }, t0) && row.Changes == 0 && !row.IsHighlighted, "row: the first reading is no change");
        Equal(row.Hex, "0x00000010", "row: hex");
        Check(!row.Apply(new DevMemReading { Value = 0x10 }, t0.AddSeconds(1)), "row: the same value is no change");
        Check(row.Apply(new DevMemReading { Value = 0x11 }, t0.AddSeconds(2)) && row.Changes == 1 && row.IsHighlighted && row.Previous == 0x10, "row: a new value is a change, highlighted");
        Equal(row.ChangedBits, "0x00000001", "row: the bits that changed");
        Check(!row.UpdateHighlight(t0.AddSeconds(3), TimeSpan.FromSeconds(3)) && row.IsHighlighted, "row: still highlighted within the time");
        Check(row.UpdateHighlight(t0.AddSeconds(6), TimeSpan.FromSeconds(3)) && !row.IsHighlighted, "row: the highlight ends");
        row.Apply(new DevMemReading { Error = "bus error: nothing answers" }, t0.AddSeconds(7));
        Check(row.HasError && row.Value == 0x11 && row.Hex == "0x00000011", "row: an error keeps the last value");
        row.Apply(new DevMemReading { Value = 0x11 }, t0.AddSeconds(8));
        Check(!row.HasError, "row: a good reading clears the error");
        row.ResetChanges();
        Check(row.Changes == 0 && row.Previous == null && !row.IsHighlighted, "row: the changes are forgotten");
        MemoryRow byteRow = new MemoryRow(0, 8);
        byteRow.Apply(new DevMemReading { Value = 0x1FF }, t0);
        Equal(byteRow.Hex, "0xFF", "row: the value is masked to the width");

        List<MemoryRow> region = MemoryRow.Region(0x43C00000, 4, 16);
        Check(region.Select(r => r.Address).SequenceEqual(new ulong[] { 0x43C00000, 0x43C00002, 0x43C00004, 0x43C00006 }) && region.All(r => r.Width == 16 && r.Watch), "region: consecutive registers of the width");
        bool threw = false;
        try
        {
            MemoryRow.Region(ulong.MaxValue - 4, 4, 32);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "region: past the end of the address space");
        MemoryRow named = new MemoryRow(0xF8000240, 32) { Name = "FPGA_RST_CTRL, \"PL\"" };
        named.Apply(new DevMemReading { Value = 0xF }, t0);
        Equal(named.ToCsv(), "0xF8000240,\"FPGA_RST_CTRL, \"\"PL\"\"\",32,0x0000000F,15,15,0000 0000 0000 0000 0000 0000 0000 1111,....,2.1E-44,0,", "csv: a line with quoting");
    }

    private static void WorkspaceTests()
    {
        List<MemoryRow> rows = MemoryRow.Region(0x43C00000, 3, 32);
        rows[0].Name = "CTRL";
        rows[1].Watch = false;
        rows.Add(new MemoryRow(0xF8000240, 32) { Name = "FPGA_RST_CTRL" });
        MemoryWorkspace workspace = MemoryWorkspace.FromRows(rows);
        workspace.RefreshMilliseconds = 250;
        workspace.Columns = "Hex,Binary";
        string path = Path.Combine(_work, "list.xml");
        workspace.Save(path);
        MemoryWorkspace read = MemoryWorkspace.Load(path);
        List<MemoryRow> back = read.ToRows(out List<string> skipped);
        Check(skipped.Count == 0 && back.Count == 4 && back[0].Name == "CTRL" && !back[1].Watch && back[3].Address == 0xF8000240 && back[3].Name == "FPGA_RST_CTRL", "workspace: the registers come back");
        Check(read.RefreshMilliseconds == 250 && read.ColumnNames.SequenceEqual(new[] { "Hex", "Binary" }), "workspace: the settings come back");
        Check(back[3].OffsetText == "+0xB4400240", "workspace: offsets from the first register");
        read.Registers.Add(new MemoryWorkspaceEntry { Address = "zz", Width = 32 });
        read.Registers.Add(new MemoryWorkspaceEntry { Address = "0x10", Width = 12 });
        read.ToRows(out skipped);
        Check(skipped.Count == 2, "workspace: bad entries are skipped");
        File.WriteAllText(path, "<GlobalSettings/>");
        bool threw = false;
        try
        {
            MemoryWorkspace.Load(path);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Check(threw, "workspace: another file is refused");
    }

    private static string _fake;

    /// <summary>A folder with devmem, devmem2 and busybox that run the stand-in, and the memory file.</summary>
    private static string FakeTools()
    {
        if (_fake != null)
        {
            return _fake;
        }
        _fake = Path.Combine(_work, "fakebin");
        Directory.CreateDirectory(_fake);
        string script = Path.Combine(AppContext.BaseDirectory, "fake-devmem.py");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string name in new[] { "devmem", "devmem2", "busybox" })
        {
            File.CreateSymbolicLink(Path.Combine(_fake, name), script);
        }
        return _fake;
    }

    private static void ProbeCommandTests(string busybox)
    {
        string fake = FakeTools();
        string grep = new[] { "/usr/bin/grep", "/bin/grep" }.First(File.Exists);
        foreach ((string present, DevMemTool expected) in new[] { ("devmem", DevMemTool.DevMem), ("busybox", DevMemTool.BusyBoxDevMem), ("devmem2", DevMemTool.DevMem2), ("", DevMemTool.None) })
        {
            string bin = Path.Combine(_work, "probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bin);
            File.CreateSymbolicLink(Path.Combine(bin, "grep"), grep);
            File.CreateSymbolicLink(Path.Combine(bin, "python3"), new[] { "/usr/bin/python3", "/bin/python3", "/usr/local/bin/python3" }.First(File.Exists));
            if (present.Length > 0)
            {
                File.CreateSymbolicLink(Path.Combine(bin, present), Path.Combine(fake, present));
            }
            int exit = RunWithPath("PATH=" + bin + "; " + DevMem.ProbeCommand, bin, null, out string output, out string error);
            Equal(DevMem.FromProbe(output.Split('\n')), (DevMemTool?)expected, "probe command: " + (present.Length > 0 ? present : "nothing") + " is found as " + expected + error.Trim());
        }
    }

    private static void ShellTests(string busybox, DevMemTool tool)
    {
        string label = (busybox == null ? "sh" : "BusyBox sh") + ", " + DevMem.ToolName(tool);
        string memory = Path.Combine(_work, "mem-" + Guid.NewGuid().ToString("N"));
        byte[] content = Enumerable.Range(0, 256).Select(i => (byte)(i * 7 + 3)).ToArray();
        File.WriteAllBytes(memory, content);
        Dictionary<string, string> environment = new Dictionary<string, string> { ["FAKE_MEM"] = memory, ["FAKE_BASE"] = "43C00000" };
        ulong Expected(int offset, int width)
        {
            ulong value = 0;
            for (int i = width / 8 - 1; i >= 0; i--)
            {
                value = (value << 8) | content[offset + i];
            }
            return value;
        }

        foreach (int width in DevMem.Widths(tool))
        {
            List<MemoryRow> rows = MemoryRow.Region(0x43C00000, 8, width);
            string command = DevMem.BuildReadCommand(tool, rows.Select(r => r.Address), width, false);
            int exit = Run(busybox, command, environment, out string output, out string error);
            List<DevMemReading> readings = DevMem.ParseReadOutput(output.Split('\n'));
            Check(exit == 0 && readings.Count == 8 && readings.All(r => r.Error == null), label + ", " + width + " bits: eight registers in one command (" + error.Trim() + output.Substring(0, Math.Min(200, output.Length)) + ")");
            Check(readings.Select((r, i) => r.Value == Expected(i * width / 8, width) && r.Address == rows[i].Address).All(ok => ok), label + ", " + width + " bits: every value and its address");
        }

        // A write, then the read-back.
        string write = DevMem.BuildWriteCommand(tool, 0x43C00010, 32, 0xDEADBEEF, false);
        Run(busybox, write, environment, out string writeOutput, out _);
        Check(DevMem.ParseWriteStatus(writeOutput.Split('\n'), out string message), label + ": the write succeeds (" + message + ")");
        List<DevMemReading> back = DevMem.ParseReadOutput(writeOutput.Split('\n'));
        Check(back.Count == 1 && back[0].Value == 0xDEADBEEF && back[0].Address == 0x43C00010, label + ": the read-back shows the new value");
        Check(File.ReadAllBytes(memory).Skip(0x10).Take(4).SequenceEqual(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE }), label + ": the memory holds it, little-endian");
        string write8 = DevMem.BuildWriteCommand(tool, 0x43C00021, 8, 0x5A, false);
        Run(busybox, write8, environment, out writeOutput, out _);
        Check(DevMem.ParseWriteStatus(writeOutput.Split('\n'), out _) && File.ReadAllBytes(memory)[0x21] == 0x5A && File.ReadAllBytes(memory)[0x20] == content[0x20], label + ": a byte write touches that byte only");

        // A register with read-only bits: the read-back differs from what was written.
        Dictionary<string, string> readOnly = new Dictionary<string, string>(environment) { ["FAKE_READONLY_MASK"] = "FFFF0000" };
        Run(busybox, DevMem.BuildWriteCommand(tool, 0x43C00030, 32, 0x12345678, false), readOnly, out writeOutput, out _);
        back = DevMem.ParseReadOutput(writeOutput.Split('\n'));
        Check(back[0].Value.HasValue && (back[0].Value.Value & 0xFFFF) == 0x5678 && back[0].Value.Value != 0x12345678, label + ": the read-back shows read-only bits as they are");

        // A bus error at one address does not lose the others.
        Dictionary<string, string> bus = new Dictionary<string, string>(environment) { ["FAKE_BUS"] = "43C00008" };
        Run(busybox, DevMem.BuildReadCommand(tool, new ulong[] { 0x43C00004, 0x43C00008, 0x43C0000C }, 32, false), bus, out string busOutput, out _);
        List<DevMemReading> busReadings = DevMem.ParseReadOutput(busOutput.Split('\n'));
        Check(busReadings.Count == 3 && busReadings[0].Value.HasValue && busReadings[1].Error != null && busReadings[1].Error.StartsWith("bus error") && busReadings[2].Value.HasValue, label + ": a bus error belongs to its address (" + busReadings[1].Error + ")");

        // No access to /dev/mem.
        Dictionary<string, string> deny = new Dictionary<string, string>(environment) { ["FAKE_DENY"] = "1" };
        Run(busybox, DevMem.BuildReadCommand(tool, new ulong[] { 0x43C00004 }, 32, false), deny, out string denyOutput, out _);
        List<DevMemReading> denied = DevMem.ParseReadOutput(denyOutput.Split('\n'));
        Check(denied.Count == 1 && denied[0].Error != null && denied[0].Error.StartsWith("permission denied"), label + ": no access to /dev/mem is explained (" + denied.FirstOrDefault()?.Error + ")");
        Run(busybox, DevMem.BuildWriteCommand(tool, 0x43C00004, 32, 1, false), deny, out writeOutput, out _);
        Check(!DevMem.ParseWriteStatus(writeOutput.Split('\n'), out message) && message.StartsWith("permission denied"), label + ": a refused write is reported");
    }

    private static int Run(string busybox, string command, Dictionary<string, string> environment, out string stdout, out string stderr)
    {
        string fake = FakeTools();
        string shellCommand = busybox != null && command.StartsWith("/bin/sh -c ", StringComparison.Ordinal) ? busybox + " sh -c " + command.Substring("/bin/sh -c ".Length) : command;
        return RunWithPath(shellCommand, fake + ":" + Environment.GetEnvironmentVariable("PATH"), environment, out stdout, out stderr);
    }

    private static int RunWithPath(string command, string path, Dictionary<string, string> environment, out string stdout, out string stderr)
    {
        ProcessStartInfo start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        start.Environment["PATH"] = path;
        foreach (KeyValuePair<string, string> pair in environment ?? new Dictionary<string, string>())
        {
            start.Environment[pair.Key] = pair.Value;
        }
        using Process process = Process.Start(start);
        System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        stdout = output.Result;
        stderr = error.Result;
        return process.ExitCode;
    }
}
