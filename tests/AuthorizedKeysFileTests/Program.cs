using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmarTTY;
using SmarTTY.Ssh;

// Regression harness for the authorized_keys text handling: finding a key, removing exactly that key without
// touching anything else, and the home directory pick.
// Run with: dotnet run --project tests/AuthorizedKeysFileTests
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

    private static byte[] Blob(string keyType, int seed)
    {
        byte[] type = Encoding.ASCII.GetBytes(keyType);
        byte[] blob = new byte[4 + type.Length + 32];
        blob[3] = (byte)type.Length;
        type.CopyTo(blob, 4);
        for (int i = 0; i < 32; i++)
        {
            blob[4 + type.Length + i] = (byte)(seed * 31 + i);
        }
        return blob;
    }

    private static string B64(byte[] blob)
    {
        return Convert.ToBase64String(blob);
    }

    private static int Main()
    {
        byte[] mine = Blob("ssh-ed25519", 1);
        byte[] other = Blob("ssh-ed25519", 2);
        byte[] third = Blob("ssh-rsa", 3);

        Check(AuthorizedKeysFile.LineHoldsKey("ssh-ed25519 " + B64(mine) + " me@host", mine), "find: key with a comment");
        Check(AuthorizedKeysFile.ParseKeyBlob("ssh-ed25519 " + B64(mine) + " me@host").SequenceEqual(mine), "parse: blob of a line with a comment");
        Check(AuthorizedKeysFile.ParseKeyBlob("command=\"x y\" ssh-ed25519 " + B64(mine)).SequenceEqual(mine), "parse: blob of a line with options");
        Check(AuthorizedKeysFile.ParseKeyBlob("# just a comment") == null && AuthorizedKeysFile.ParseKeyBlob("") == null, "parse: no key, no blob");
        Check(AuthorizedKeysFile.LineHoldsKey("ssh-ed25519 " + B64(mine), mine), "find: key without a comment");
        Check(AuthorizedKeysFile.LineHoldsKey("ssh-ed25519 " + B64(mine) + " a comment with blanks", mine), "find: comment with blanks");
        Check(AuthorizedKeysFile.LineHoldsKey("from=\"10.0.0.0/8\",no-pty ssh-ed25519 " + B64(mine) + " me@host", mine), "find: key with options");
        Check(AuthorizedKeysFile.LineHoldsKey("command=\"echo hi there\" ssh-ed25519 " + B64(mine), mine), "find: options with a quoted blank, no comment");
        Check(AuthorizedKeysFile.LineHoldsKey("  ssh-ed25519\t" + B64(mine) + "\tme", mine), "find: tabs and leading blanks");
        Check(!AuthorizedKeysFile.LineHoldsKey("ssh-ed25519 " + B64(other) + " me@host", mine), "find: another key is not it");
        Check(!AuthorizedKeysFile.LineHoldsKey("# ssh-ed25519 " + B64(mine) + " disabled", mine), "find: a commented out key does not count");
        Check(!AuthorizedKeysFile.LineHoldsKey("", mine), "find: blank line");
        Check(!AuthorizedKeysFile.LineHoldsKey("ssh-ed25519 not*base64*at*all==== x", mine), "find: garbage does not throw");
        Check(!AuthorizedKeysFile.LineHoldsKey("ssh-ed25519 " + B64(mine), null), "find: no blob given");

        string keyLine = "ssh-ed25519 " + B64(mine) + " me@host";
        Check(AuthorizedKeysFile.IsIncompleteCopy(keyLine.Substring(0, 30), keyLine), "incomplete copy: the beginning of the key line");
        Check(!AuthorizedKeysFile.IsIncompleteCopy(keyLine, keyLine), "incomplete copy: the whole line is not one");
        Check(!AuthorizedKeysFile.IsIncompleteCopy("ssh-ed25519 " + B64(other), keyLine), "incomplete copy: another key is not one");
        Check(!AuthorizedKeysFile.IsIncompleteCopy("", keyLine) && !AuthorizedKeysFile.IsIncompleteCopy(null, keyLine) && !AuthorizedKeysFile.IsIncompleteCopy("ssh", null), "incomplete copy: empty input");

        List<string> file = new List<string>
        {
            "# keys of the build servers",
            "",
            "ssh-ed25519 " + B64(other),
            "ssh-ed25519 " + B64(mine) + " me@host",
            "ssh-rsa " + B64(third) + " old@laptop",
            "#disabled",
            "command=\"/usr/bin/backup\" ssh-ed25519 " + B64(other) + " backup",
            "ssh-ed25519 " + B64(mine),
        };
        List<string> remaining = AuthorizedKeysFile.RemoveKey(file, mine, out bool removed);
        Check(removed, "remove: reports that something was removed");
        Check(remaining.Count == file.Count - 2, "remove: both lines of the key (with and without comment) are gone");
        Check(remaining.SequenceEqual(new[] { file[0], file[1], file[2], file[4], file[5], file[6] }), "remove: every other line stays, in order");
        Check(remaining.Contains("") && remaining.Contains("#disabled") && remaining.Contains(file[2]), "remove: blank lines, short comments and comment-less keys survive");

        remaining = AuthorizedKeysFile.RemoveKey(file, Blob("ssh-ed25519", 99), out removed);
        Check(!removed && remaining.SequenceEqual(file), "remove: an unknown key changes nothing");
        Check(AuthorizedKeysFile.ContainsKey(file, third) && !AuthorizedKeysFile.ContainsKey(file, Blob("ssh-rsa", 7)), "contains: present and absent");

        Check(AuthorizedKeysFile.PickHomeDirectory(new[] { "Welcome to host!", "/home/user" }) == "/home/user", "home: banner text before pwd is skipped");
        Check(AuthorizedKeysFile.PickHomeDirectory(new[] { "/root " }) == "/root", "home: surrounding blanks are trimmed");
        Check(AuthorizedKeysFile.PickHomeDirectory(new[] { "no path here" }) == null, "home: nothing that looks like a path");
        Check(AuthorizedKeysFile.PickHomeDirectory(null) == null, "home: null input");

        // Fingerprints of a host key: the expected texts are what ssh-keygen -l prints for the same ed25519 public key
        // (AAAAC3NzaC1lZDI1NTE5AAAAILTAwIfu28sItna409oruYx5zLvtRCc6QfQ8Dwa0z6b+).
        byte[] hostBlob = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAILTAwIfu28sItna409oruYx5zLvtRCc6QfQ8Dwa0z6b+");
        Check(HostKeyFingerprint.Sha256(hostBlob) == "SHA256:NnwNL+UHTy3IZZclD0I27FqnsJ/0cSvPZzsCAxU/nEE", "fingerprint: SHA256 matches ssh-keygen (unpadded base64)");
        Check(HostKeyFingerprint.Md5(hostBlob) == "MD5:af:4f:15:33:2d:6a:1b:38:ca:c8:42:21:c0:79:b5:d2", "fingerprint: MD5 matches ssh-keygen -E md5");
        Check(HostKeyFingerprint.Describe(hostBlob) == HostKeyFingerprint.Sha256(hostBlob) + Environment.NewLine + HostKeyFingerprint.Md5(hostBlob), "fingerprint: both are shown, SHA256 first");
        Check(HostKeyFingerprint.Describe(null) == null && HostKeyFingerprint.Describe(new byte[0]) == null, "fingerprint: no key, no text");
        // Keyboard-interactive login: the password answers the password prompt, the rest is left for the user.
        string[] responses = new string[2];
        KeyboardInteractiveAnswers answers = new KeyboardInteractiveAnswers("secret");
        List<int> forUser = answers.Fill(new[] { "Password: ", "Verification code: " }, responses);
        Check(responses[0] == "secret" && responses[1] == null && forUser.SequenceEqual(new[] { 1 }), "kbd: the password answers its prompt, the code is left for the user");
        Check(answers.PasswordGiven, "kbd: the password is remembered as sent");

        answers = new KeyboardInteractiveAnswers("secret");
        responses = new string[2];
        forUser = answers.Fill(new[] { "Duo passcode or option (1-3): ", "Password:" }, responses);
        Check(responses[0] == null && responses[1] == "secret" && forUser.SequenceEqual(new[] { 0 }), "kbd: the password goes to the password prompt wherever it is");

        // Two rounds, the code only in the second one (PAM with a second module).
        answers = new KeyboardInteractiveAnswers("secret");
        responses = new string[1];
        forUser = answers.Fill(new[] { "Password:" }, responses);
        Check(responses[0] == "secret" && forUser.Count == 0, "kbd: round one is answered by the password alone");
        responses = new string[1];
        forUser = answers.Fill(new[] { "Verification code:" }, responses);
        Check(responses[0] == null && forUser.SequenceEqual(new[] { 0 }), "kbd: round two asks the user, the password is not sent as the code");

        // A server in another language: the first prompt of the first round is still taken for the password.
        answers = new KeyboardInteractiveAnswers("secret");
        responses = new string[1];
        forUser = answers.Fill(new[] { "Kennwort:" }, responses);
        Check(responses[0] == "secret" && forUser.Count == 0, "kbd: an unrecognized first prompt gets the password as before");
        answers = new KeyboardInteractiveAnswers("secret");
        responses = new string[2];
        forUser = answers.Fill(new[] { "Kennwort:", "Code:" }, responses);
        Check(responses[0] == "secret" && forUser.SequenceEqual(new[] { 1 }), "kbd: a second unrecognized prompt is left for the user");

        // Expired password: the old password answers the current-password prompt, the new one is asked from the user.
        answers = new KeyboardInteractiveAnswers("secret");
        responses = new string[3];
        forUser = answers.Fill(new[] { "(current) UNIX password:", "New password:", "Retype new password:" }, responses);
        Check(responses[0] == "secret" && responses[1] == null && responses[2] == null && forUser.SequenceEqual(new[] { 1, 2 }), "kbd: the old password is never sent as the new one");

        // Only one prompt of a round gets the password.
        answers = new KeyboardInteractiveAnswers("secret");
        responses = new string[2];
        forUser = answers.Fill(new[] { "Password:", "Password:" }, responses);
        Check(responses[0] == "secret" && responses[1] == null && forUser.SequenceEqual(new[] { 1 }), "kbd: the password answers one prompt per round");

        // Second step of a chain (password accepted, then keyboard-interactive): an unrecognized prompt is not the password.
        answers = new KeyboardInteractiveAnswers("secret", passwordAlreadyAccepted: true);
        responses = new string[1];
        forUser = answers.Fill(new[] { "Enter the 6 digits:" }, responses);
        Check(responses[0] == null && forUser.SequenceEqual(new[] { 0 }) && answers.PasswordGiven, "kbd chain: an unrecognized prompt after the password step goes to the user");
        answers = new KeyboardInteractiveAnswers("secret", passwordAlreadyAccepted: true);
        responses = new string[1];
        forUser = answers.Fill(new[] { "Password:" }, responses);
        Check(responses[0] == "secret" && forUser.Count == 0, "kbd chain: a prompt that asks for the password again still gets it");

        foreach (string codePrompt in new[] { "Verification code:", "Enter PIN:", "One-time password (OATH) for `user':", "Authenticator code:", "Response:", "Token:", "SMS passcode:" })
        {
            Check(KeyboardInteractiveAnswers.NeedsUser(codePrompt) && !KeyboardInteractiveAnswers.LooksLikePasswordPrompt(codePrompt), "kbd: '" + codePrompt + "' is for the user");
        }
        foreach (string passwordPrompt in new[] { "Password:", "user@host's password:", "Password for root:", "Passwort:", "Mot de passe :", "\u041f\u0430\u0440\u043e\u043b\u044c:" })
        {
            Check(KeyboardInteractiveAnswers.LooksLikePasswordPrompt(passwordPrompt), "kbd: '" + passwordPrompt + "' is a password prompt");
        }
        responses = new string[1];
        new KeyboardInteractiveAnswers(null).Fill(new[] { "Password:" }, responses);
        Check(responses[0] == "", "kbd: no password is an empty answer, not null");

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
