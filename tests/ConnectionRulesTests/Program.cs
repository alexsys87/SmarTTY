using System;
using SmarTTY.Ssh;

// Checks of the decisions a connection attempt makes without a server: what a saved host key says about the key the
// server presents, the split of the proxy credentials, the first password and the keep-alive default.
// Run with: dotnet run --project tests/ConnectionRulesTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static int Main()
    {
        byte[] key = { 1, 2, 3, 4, 5 };
        byte[] otherKey = { 1, 2, 3, 4, 6 };
        string saved = Convert.ToBase64String(key);

        // The host key of a known server.
        ConnectionRules.HostKeyVerdict verdict = ConnectionRules.CompareHostKey("ssh-ed25519", saved, "ssh-ed25519", key, out string reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Matches && reason == null, "host key: the saved key is accepted without a question");
        verdict = ConnectionRules.CompareHostKey("ssh-ed25519", saved, "ssh-ed25519", otherKey, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Changed && reason == null, "host key: another key of the same type is a change without a type reason");
        verdict = ConnectionRules.CompareHostKey("ssh-rsa", saved, "ssh-ed25519", key, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Changed && reason == "Key type changed from ssh-rsa to ssh-ed25519", "host key: a changed type is named for the user");
        verdict = ConnectionRules.CompareHostKey("6", saved, "ssh-ed25519", key, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Matches, "host key: a type saved as a libssh2 number (6) is read as ed25519");
        verdict = ConnectionRules.CompareHostKey("4", saved, "ecdsa-sha2-nistp384", key, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Matches, "host key: libssh2 type 4 is ecdsa-sha2-nistp384");
        verdict = ConnectionRules.CompareHostKey("5", saved, "ecdsa-sha2-nistp521", key, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Matches, "host key: libssh2 type 5 is ecdsa-sha2-nistp521");
        verdict = ConnectionRules.CompareHostKey("6", saved, "ssh-rsa", key, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Changed && reason == "Key type changed from 6 to ssh-rsa", "host key: the reason shows the type as it was saved");
        verdict = ConnectionRules.CompareHostKey("ssh-ed25519", "this is not base64!", "ssh-ed25519", key, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Changed && reason != null && reason.Contains("cannot be read"), "host key: a damaged saved key is asked about again, not a decoding error");
        verdict = ConnectionRules.CompareHostKey("ssh-ed25519", null, "ssh-ed25519", key, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Changed, "host key: a saved entry without a key is a change");
        verdict = ConnectionRules.CompareHostKey("ssh-ed25519", saved, "ssh-ed25519", null, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Changed, "host key: a server that presented no key data is not accepted");
        verdict = ConnectionRules.CompareHostKey("ssh-ed25519", saved, "ssh-ed25519", new byte[] { 1, 2, 3, 4 }, out reason);
        Check(verdict == ConnectionRules.HostKeyVerdict.Changed, "host key: a shorter key is not a match");

        // Proxy credentials "user:password".
        ConnectionRules.SplitProxyCredentials("alice:secret", out string user, out string password);
        Check(user == "alice" && password == "secret", "proxy credentials: user and password");
        ConnectionRules.SplitProxyCredentials("alice:se:cr:et", out user, out password);
        Check(user == "alice" && password == "se:cr:et", "proxy credentials: the password may contain colons");
        ConnectionRules.SplitProxyCredentials("alice", out user, out password);
        Check(user == "alice" && password == null, "proxy credentials: a user without a password");
        ConnectionRules.SplitProxyCredentials("alice:", out user, out password);
        Check(user == "alice" && password == "", "proxy credentials: an empty password is kept as empty");
        ConnectionRules.SplitProxyCredentials(":secret", out user, out password);
        Check(user == "" && password == "secret", "proxy credentials: an empty user name is kept");
        ConnectionRules.SplitProxyCredentials(null, out user, out password);
        Check(user == null && password == null, "proxy credentials: none");
        ConnectionRules.SplitProxyCredentials("", out user, out password);
        Check(user == null && password == null, "proxy credentials: empty text is none");

        // The first password: never an empty guess when somebody can be asked.
        Check(ConnectionRules.FirstPasswordToTry(null, canAskUser: true) == null, "first password: nothing saved, the user is asked before anything is sent");
        Check(ConnectionRules.FirstPasswordToTry(null, canAskUser: false) == "", "first password: nobody to ask, the empty password is tried");
        Check(ConnectionRules.FirstPasswordToTry("secret", canAskUser: true) == "secret", "first password: a known password is sent");
        Check(ConnectionRules.FirstPasswordToTry("", canAskUser: true) == "", "first password: an empty password the user confirmed is sent as it is");

        // Keep-alive: 0 is "not set", negative is "off".
        Check(ConnectionRules.KeepAliveSeconds(0, 15) == 15, "keep-alive: a profile that does not say gets the default");
        Check(ConnectionRules.KeepAliveSeconds(-1, 15) == 0, "keep-alive: a profile that switched it off keeps it off");
        Check(ConnectionRules.KeepAliveSeconds(-30, 15) == 0, "keep-alive: any negative value is off");
        Check(ConnectionRules.KeepAliveSeconds(60, 15) == 60, "keep-alive: the interval of the profile is used");
        Check(ConnectionRules.KeepAliveSeconds(1, 15) == 1, "keep-alive: one second is not mistaken for 'not set'");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
