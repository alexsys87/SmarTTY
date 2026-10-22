using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Linq;

// Regression harness for the U-Boot firmware upload helpers (IO/UBootFirmwareScript.cs): checking of the values that end up
// on the board's command line, building of the command list, and recognition of the board's prompt in its output.
// Run with: dotnet run --project tests/UBootFirmwareScriptTests
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new List<string>();

    private static void Check(string name, bool condition, string detail = null)
    {
        if (condition)
        {
            _passed++;
        }
        else
        {
            Failures.Add(name);
            Console.WriteLine("FAIL: " + name + (detail == null ? string.Empty : " - " + detail));
        }
    }

    private static UBootFirmwareSettings Settings(string after = "")
    {
        return new UBootFirmwareSettings
        {
            ServerIp = "192.168.1.2",
            DeviceIp = "192.168.1.10",
            LoadAddress = "0x80060000",
            FileName = "fw.bin",
            AfterCommands = after
        };
    }

    private static void Validation()
    {
        Check("a normal address is valid", UBootFirmwareScript.IsValidIPv4("192.168.1.2") && UBootFirmwareScript.IsValidIPv4(" 10.0.0.1 "));
        foreach (string bad in new[] { "", null, "1.2.3", "1.2.3.4.5", "256.1.1.1", "a.b.c.d", "1.2.3.-4", "192.168.001.002", "1.2.3.4;reset", "1..3.4" })
        {
            Check("an invalid address is refused: " + (bad ?? "null"), !UBootFirmwareScript.IsValidIPv4(bad));
        }

        foreach (string good in new[] { "fw.bin", "openwrt-ramips-mt7621-sysupgrade.bin", "dir/sub/image_1.2+rc@x.itb", "a,b" })
        {
            Check("a file name is valid: " + good, UBootFirmwareScript.IsValidFileName(good));
        }
        foreach (string bad in new[] { "", null, "my fw.bin", "fw.bin;reset", "fw$x", "fw'x", "fw\"x", "-fw.bin", "fw|x", "fw&x", "fw\\x", "прошивка.bin", new string('a', 201) })
        {
            Check("a file name is refused: " + (bad ?? "null"), !UBootFirmwareScript.IsValidFileName(bad));
        }

        foreach (string good in new[] { "0x80060000", "80060000", "0X1F", "${loadaddr}", " 0x81000000 " })
        {
            Check("a load address is valid: " + good, UBootFirmwareScript.IsValidLoadAddress(good));
        }
        foreach (string bad in new[] { "", null, "0x", "0xZZ", "0x80060000;reset", "$loadaddr", "${}", "${a b}", "0x123456789012345678" })
        {
            Check("a load address is refused: " + (bad ?? "null"), !UBootFirmwareScript.IsValidLoadAddress(bad));
        }

        Check("good settings validate", UBootFirmwareScript.Validate(Settings()) == null);
        UBootFirmwareSettings s = Settings();
        s.DeviceIp = "";
        Check("the device address may be empty", UBootFirmwareScript.Validate(s) == null);
        s.DeviceIp = "bad";
        Check("a bad device address is reported", UBootFirmwareScript.Validate(s) != null);
        s = Settings();
        s.ServerIp = "";
        Check("the server address is required", UBootFirmwareScript.Validate(s) != null);
        s = Settings("sf probe\nsf write {addr} <offset> ${filesize}");
        string problem = UBootFirmwareScript.Validate(s);
        Check("an unfilled <offset> is reported", problem != null && problem.Contains("<offset>"), problem);
        s = Settings("echo " + new string('x', 260));
        Check("a command that is too long is reported", UBootFirmwareScript.Validate(s) != null);
        s = Settings("echo a\u0001b");
        Check("a control character is reported", UBootFirmwareScript.Validate(s) != null);
        s = Settings("sf write {addr} 0x50000 ${filesize}");
        Check("a filled command is fine", UBootFirmwareScript.Validate(s) == null);
    }

    private static void Building()
    {
        List<UBootStep> steps = UBootFirmwareScript.Build(Settings());
        Check("steps without follow-up commands", steps.Select(x => x.Command).SequenceEqual(new[]
        {
            "setenv ipaddr 192.168.1.10",
            "setenv serverip 192.168.1.2",
            "tftpboot 0x80060000 fw.bin"
        }), string.Join(" | ", steps.Select(x => x.Command)));
        Check("step kinds", steps.Select(x => x.Kind).SequenceEqual(new[] { UBootStepKind.Setup, UBootStepKind.Setup, UBootStepKind.Download }));

        UBootFirmwareSettings s = Settings("# a comment\r\nbootm {addr}\n\n   go {addr}  \nsetenv x {file}-{serverip}-{ipaddr}-{FILE}");
        steps = UBootFirmwareScript.Build(s);
        Check("follow-up commands are expanded, trimmed and comments dropped", steps.Skip(3).Select(x => x.Command).SequenceEqual(new[]
        {
            "bootm 0x80060000",
            "go 0x80060000",
            "setenv x fw.bin-192.168.1.2-192.168.1.10-fw.bin"
        }), string.Join(" | ", steps.Skip(3).Select(x => x.Command)));
        Check("follow-up steps have their own kind", steps.Skip(3).All(x => x.Kind == UBootStepKind.After));

        s = Settings("echo {ipaddr}");
        s.DeviceIp = "";
        steps = UBootFirmwareScript.Build(s);
        Check("without a device address the board's own is used", steps.Count == 3 && steps[0].Command == "setenv serverip 192.168.1.2" && steps[2].Command == "echo ${ipaddr}",
            string.Join(" | ", steps.Select(x => x.Command)));

        s = Settings("sf write ${fileaddr} 0x0 ${filesize}; echo ${loadaddr} {unknown}");
        steps = UBootFirmwareScript.Build(s);
        Check("U-Boot's own ${variables} and unknown {words} are left alone", steps[3].Command == "sf write ${fileaddr} 0x0 ${filesize}; echo ${loadaddr} {unknown}", steps[3].Command);

        s = Settings();
        s.LoadAddress = "${loadaddr}";
        Check("a variable as the load address", UBootFirmwareScript.Build(s)[2].Command == "tftpboot ${loadaddr} fw.bin");
    }

    private static void Destructive()
    {
        Check("flash commands are flagged", UBootFirmwareScript.IsDestructive("sf erase 0x0 +${filesize}") && UBootFirmwareScript.IsDestructive("nand write 0x1 0x2 0x3") && UBootFirmwareScript.IsDestructive("saveenv") && UBootFirmwareScript.IsDestructive("fatwrite mmc 0:1 0x1 a.bin 0x10"));
        Check("boot commands are not", !UBootFirmwareScript.IsDestructive("bootm 0x80060000") && !UBootFirmwareScript.IsDestructive("go 0x80060000") && !UBootFirmwareScript.IsDestructive("printenv"));
    }

    private static void StartingCommands()
    {
        foreach (string c in new[] { "bootm 0x80060000", "go 0x1000", "reset", "boot", "bootz ${loadaddr} - ${fdtaddr}", "  BOOTM 1", "sf write 1 2 3; reset", "echo x;\tbootm" })
        {
            Check("a command that starts the board: " + c, UBootFirmwareScript.NeverReturns(c));
        }
        foreach (string c in new[] { "", null, "sf probe", "tftpboot 0x1 fw.bin", "echo bootm", "setenv bootcmd bootm", "printenv bootargs", "bootmenu_x 1", "gox" })
        {
            Check("a command that returns: " + (c ?? "null"), !UBootFirmwareScript.NeverReturns(c));
        }

        string problem = UBootFirmwareScript.Validate(Settings("bootm {addr}\nsf probe"));
        Check("a starting command in the middle is refused", problem != null && problem.Contains("last"), problem);
        Check("a starting command at the end is fine", UBootFirmwareScript.Validate(Settings("sf probe\nbootm {addr}")) == null);
        Check("a starting command alone is fine", UBootFirmwareScript.Validate(Settings("go {addr}")) == null);
    }

    private static void CommandOutput()
    {
        Check("a flash erase failure is recognized", UBootFirmwareScript.FindCommandProblem("\r\nSF: Erase failed (error -5)\r\n=> ") == "SF: Erase failed (error -5)");
        Check("no flash is recognized", UBootFirmwareScript.FindCommandProblem("\r\nNo SPI flash selected. Please run `sf probe'\r\n=> ") != null);
        Check("usage text is recognized", UBootFirmwareScript.FindCommandProblem("\r\nsf - SPI flash sub-system\r\n\r\nUsage:\r\nsf probe\r\n=> ") == "Usage:");
        Check("an unknown command is recognized", UBootFirmwareScript.FindCommandProblem("\r\nUnknown command 'sf' - try 'help'\r\n=> ") != null);
        Check("a good write is not a problem", UBootFirmwareScript.FindCommandProblem("\r\nSF: 8388608 bytes @ 0x0 Erased: OK\r\nSF: 8388608 bytes @ 0x0 Written: OK\r\n=> ") == null);
        Check("a probe is not a problem", UBootFirmwareScript.FindCommandProblem("\r\nSF: Detected w25q128 with page size 256 Bytes, erase size 4 KiB, total 16 MiB\r\n=> ") == null);
        Check("empty output is not a problem", UBootFirmwareScript.FindCommandProblem("") == null && UBootFirmwareScript.FindCommandProblem(null) == null);
    }

    private static void BoardOutput()
    {
        string[] prompts =
        {
            "=> ", "U-Boot> ", "ath>", "MT7621 # ", "uboot> ", "\r\nBytes transferred = 8388608 (800000 hex)\r\n=> ",
            "\u001b[0m=> ", "line one\r\nline two\r\nZynq> ", "nanopi# ", "(Re)start USB...\r\n=> "
        };
        foreach (string text in prompts)
        {
            Check("prompt recognized: " + text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\u001b", "ESC"), ConsoleOutputWatcher.EndsWithPrompt(text, string.Empty));
        }
        string[] notPrompts =
        {
            "", "Loading: #####", "Loading: ##########################", "TFTP from server 192.168.1.2; our IP address is 192.168.1.10\r\n",
            "=> setenv serverip 192.168.1.2", "Bytes transferred = 100 (64 hex)\r\n", "=> \r\n", "Using eth0 device\r\nTFTP from server 1.2.3.4"
        };
        foreach (string text in notPrompts)
        {
            Check("not a prompt: " + text.Replace("\r", "\\r").Replace("\n", "\\n"), !ConsoleOutputWatcher.EndsWithPrompt(text, string.Empty));
        }

        Check("a custom prompt is matched at the end", ConsoleOutputWatcher.EndsWithPrompt("x\r\nMyBoard> ", "MyBoard>") && ConsoleOutputWatcher.EndsWithPrompt("=> ", " => "));
        Check("a custom prompt does not match other prompts", !ConsoleOutputWatcher.EndsWithPrompt("U-Boot> ", "MyBoard>") && !ConsoleOutputWatcher.EndsWithPrompt("=> \r\n", "=>"));

        ConsoleOutputWatcher watcher = new ConsoleOutputWatcher();
        watcher.Append("TFTP from server 1.2.3.4\r\nLoading: ####\r\n", 1000);
        Check("not ready while the board is writing", !watcher.IsPromptReady(string.Empty, 1010, 150));
        watcher.Append("done\r\nBytes transferred = 12 (c hex)\r\n=> ", 2000);
        Check("not ready right after the prompt (the board may go on)", !watcher.IsPromptReady(string.Empty, 2050, 150));
        Check("ready after the quiet time", watcher.IsPromptReady(string.Empty, 2200, 150));
        Check("the output is kept", watcher.Text.Contains("Bytes transferred") && watcher.Text.Contains("Loading"));
        watcher.Reset();
        Check("reset clears the output", watcher.Text.Length == 0 && !watcher.IsPromptReady(string.Empty, 9000, 150));

        watcher.Append(new string('a', 70000), 5);
        Check("the kept output is limited", watcher.Text.Length < 70000);
        watcher.Append("\r\n=> ", 6);
        Check("the prompt is still seen after trimming", watcher.IsPromptReady(string.Empty, 1000, 150));
        Check("a null custom prompt means automatic detection", ConsoleOutputWatcher.EndsWithPrompt(watcher.Text, null));
        watcher.Append("=> ", int.MaxValue - 10);
        Check("quiet time works across the tick wrap", watcher.IsPromptReady(string.Empty, unchecked(int.MaxValue + 500), 150));

        Check("a successful download is recognized", UBootFirmwareScript.DownloadReported("Bytes transferred = 5 (5 hex)") && !UBootFirmwareScript.DownloadReported("Loading: ###"));
        Check("a failed download is recognized", UBootFirmwareScript.FindDownloadProblem("Using eth0 device\r\nTFTP from server 1.2.3.4; our IP address is 1.2.3.5\r\nLoading: T T T T\r\nRetry count exceeded; starting again\r\n=> ") == "Retry count exceeded; starting again");
        Check("an unknown command is recognized", UBootFirmwareScript.FindDownloadProblem("Unknown command 'tftpboot' - try 'help'\r\n") != null);
        Check("a clean download has no problem", UBootFirmwareScript.FindDownloadProblem("Loading: ####\r\ndone\r\nBytes transferred = 5 (5 hex)\r\n") == null);
    }

    private static int Main()
    {
        Validation();
        Building();
        Destructive();
        StartingCommands();
        CommandOutput();
        BoardOutput();
        Console.WriteLine(Failures.Count == 0 ? "All " + _passed + " checks passed." : Failures.Count + " check(s) failed, " + _passed + " passed.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
