using System;
using System.Reflection;
using Renci.SshNet;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Integration and compatibility checks for the SSH.NET dependency, run from the repository root:
///
///   tests/SshNetIntegrationTests/setup-sshd.sh   # once, starts a throwaway OpenSSH server
///   dotnet run --project tests/SshNetIntegrationTests
///
/// The SSH.NET API surface that SmarTTY reaches through reflection is always checked, because a
/// version update breaks those lookups at runtime. The live-server checks need the server above
/// and are skipped with a notice when it is not available (set SMARTTY_SSH_TEST_REQUIRED=1 to
/// turn that skip into a failure, as CI does).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Test.StartWatchdog(TimeSpan.FromMinutes(5));
        Console.WriteLine("SmarTTY SSH.NET integration tests");
        Console.WriteLine("SSH.NET " + typeof(SshClient).Assembly.GetName().Version + " from " + Assembly.GetExecutingAssembly().GetName().Name);

        SshNetContractTests.ReportVersion();
        SshNetContractTests.Run();
        PtySetupGateTests.Run();
        LocalDeleteTests.Run();
        ShellChannelStateTests.Run();
        X11ForwardingTests.Run();
        WindowsHostTests.Run();

        if (TestEnvironment.IntegrationEnabled())
        {
            TestEnvironment environment = TestEnvironment.FromVariables();
            Console.WriteLine();
            Console.WriteLine("Using the test server at " + environment.Host + ":" + environment.Port + " as " + environment.User);
            SshIntegrationTests.Run(environment);
            FileTransferManagerTests.Run(environment);
            SftpFileSystemTests.Run(environment);
            WindowsAuthorizedKeysTests.Run(environment);
            HostToHostCopyTests.Run(environment);
            X11ForwardingTests.RunLive(environment);
        }
        else
        {
            Test.Section("Live server checks");
            Console.WriteLine("SKIP: no test server is configured, only the SSH.NET API contract was checked.");
            Console.WriteLine("      Run tests/SshNetIntegrationTests/setup-sshd.sh first to exercise a real server.");
        }

        if (DropbearTests.Enabled())
        {
            DropbearTests.Run();
        }

        return Test.Report("SshNetIntegrationTests");
    }
}
