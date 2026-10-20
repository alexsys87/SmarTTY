using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmarTTY;

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

        Console.WriteLine();
        Console.WriteLine(_passed + " checks passed.");
        return 0;
    }
}
