using System;
using System.Threading;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Checks against Dropbear with a BusyBox ash login shell, the SSH server and shell of OpenWRT, provisioned by
/// setup-dropbear.sh: PTY commands started together with helper commands, the way a Smart Terminal command starts
/// together with the history write. Dropbear drops the request of a PTY channel when a helper without a PTY exits
/// while it is handled (see Ssh/PtySetupGate.cs); the race is too timing dependent to be caught reliably here, so the
/// gate itself is checked by PtySetupGateTests and this is a compatibility check of the whole path on Dropbear.
/// </summary>
internal static class DropbearTests
{
    public static bool Enabled()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMARTTY_DROPBEAR_TEST_PORT")))
        {
            return true;
        }
        if (Environment.GetEnvironmentVariable("SMARTTY_DROPBEAR_TEST_REQUIRED") == "1")
        {
            throw new InvalidOperationException("SMARTTY_DROPBEAR_TEST_REQUIRED=1 but no Dropbear server was provisioned. Run tests/SshNetIntegrationTests/setup-dropbear.sh first.");
        }
        return false;
    }

    public static void Run()
    {
        string host = Value("SMARTTY_DROPBEAR_TEST_HOST", "127.0.0.1");
        int port = int.Parse(Value("SMARTTY_DROPBEAR_TEST_PORT", "2223"));
        string user = Value("SMARTTY_DROPBEAR_TEST_USER", "smartty-ash");
        string password = Value("SMARTTY_DROPBEAR_TEST_PASSWORD", "smartty-ash-password");
        Console.WriteLine();
        Console.WriteLine("Using the Dropbear server at " + host + ":" + port + " as " + user);

        Test.Section("Dropbear: helper commands do not overlap with setting up a PTY channel");
        SshConnectionParameters parameters = new SshConnectionParameters
        {
            Host = host,
            DisplayHostName = host,
            Port = port,
            UserName = user,
            // A dropped request is only noticed through the session timeout; a short one keeps the check fast.
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using SshSession session = new SshSession(parameters);
        Test.Check(session.Authenticate(user, password), "password authentication against Dropbear");
        Test.Equal("BusyBox", (session.RunCommandChecked("ash --help 2>&1 | head -n 1", requireSuccess: false)?.Find((string line) => line.StartsWith("BusyBox")) ?? "").Split(' ')[0], "the login shell is BusyBox ash");
        // The helper exits at slightly different moments, so that some of its exits would fall into the PTY setup.
        const int iterations = 40;
        int failures = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            string helper = ((iteration % 4 == 0) ? "" : "sleep 0.00" + (iteration % 10) + "; ") + "printf x >> smartty-dropbear-race";
            Thread helperThread = new Thread(() => session.RunCommand(helper, out _));
            helperThread.Start();
            try
            {
                using SshShellChannel channel = session.OpenShellChannel("echo pty-command-ran", "xterm", ExtendedDataMode.Merge);
                string output = new System.IO.StreamReader(channel.Output).ReadToEnd();
                channel.WaitClosed();
                if (!output.Contains("pty-command-ran"))
                {
                    failures++;
                    Console.WriteLine("  iteration " + iteration + ": the command did not run: '" + output.Trim() + "'");
                }
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine("  iteration " + iteration + ": " + exception.Message);
            }
            helperThread.Join();
        }
        Test.Equal(0, failures, "a PTY command started together with a helper command always starts (" + iterations + " tries)");
        Test.Equal(iterations.ToString(), session.RunCommandChecked("wc -c < smartty-dropbear-race", requireSuccess: true)?.Find((string line) => line.Trim() != "")?.Trim(), "every helper command ran");
        session.RunCommand("rm -f smartty-dropbear-race", out _);
    }

    private static string Value(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
