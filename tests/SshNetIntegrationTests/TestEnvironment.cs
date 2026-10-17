using System;

namespace SmarTTY.Ssh.Tests;

/// <summary>
/// Connection details of the OpenSSH server the integration tests run against.
/// setup-sshd.sh provisions the server and prints these values as environment variables.
/// </summary>
internal sealed class TestEnvironment
{
    public string Host = "127.0.0.1";

    public int Port = 2222;

    public string User = "smartty-test";

    public string Password = "smartty-test-password";

    public string KeyFile;

    public string EncryptedKeyFile;

    public string KeyPassphrase = "smartty-key-passphrase";

    public string HostKeyType = "ssh-ed25519";

    public string RemoteHome => "/home/" + User;

    public SshConnectionParameters Parameters()
    {
        return new SshConnectionParameters
        {
            Host = Host,
            DisplayHostName = Host,
            Port = Port,
            UserName = User,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
    }

    public static TestEnvironment FromVariables()
    {
        TestEnvironment environment = new TestEnvironment();
        environment.Host = Value("SMARTTY_SSH_TEST_HOST", environment.Host);
        environment.Port = int.Parse(Value("SMARTTY_SSH_TEST_PORT", environment.Port.ToString()));
        environment.User = Value("SMARTTY_SSH_TEST_USER", environment.User);
        environment.Password = Value("SMARTTY_SSH_TEST_PASSWORD", environment.Password);
        environment.KeyFile = Value("SMARTTY_SSH_TEST_KEY", environment.KeyFile);
        environment.EncryptedKeyFile = Value("SMARTTY_SSH_TEST_ENCRYPTED_KEY", environment.EncryptedKeyFile);
        environment.KeyPassphrase = Value("SMARTTY_SSH_TEST_KEY_PASSPHRASE", environment.KeyPassphrase);
        environment.HostKeyType = Value("SMARTTY_SSH_TEST_HOST_KEY_TYPE", environment.HostKeyType);
        return environment;
    }

    /// <summary>
    /// The live-server checks run only when a server was provisioned; CI always provisions one and
    /// sets SMARTTY_SSH_TEST_REQUIRED=1, which turns a missing server into a failure instead of a skip.
    /// </summary>
    public static bool IntegrationEnabled()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMARTTY_SSH_TEST_PORT")))
        {
            return true;
        }
        if (Environment.GetEnvironmentVariable("SMARTTY_SSH_TEST_REQUIRED") == "1")
        {
            throw new InvalidOperationException("SMARTTY_SSH_TEST_REQUIRED=1 but no test server was provisioned. Run tests/SshNetIntegrationTests/setup-sshd.sh first.");
        }
        return false;
    }

    public string RemoteFile(string name)
    {
        return RemoteHome + "/" + name;
    }

    private static string Value(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
