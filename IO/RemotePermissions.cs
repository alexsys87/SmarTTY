using System;
using System.Collections.Generic;

namespace SmarTTY.IO;

/// <summary>
/// Unix permission bits of a file or folder, as the file list reads them from ls and changes them with chmod: the mode
/// column of ls, the symbolic ("rwxr-xr-x") and octal ("755") forms.
/// </summary>
public static class RemotePermissions
{
    public const int SetUid = 0x800;      // 04000

    public const int SetGid = 0x400;      // 02000

    public const int Sticky = 0x200;      // 01000

    public const int OwnerRead = 0x100;   // 0400

    public const int OwnerWrite = 0x80;   // 0200

    public const int OwnerExecute = 0x40; // 0100

    public const int GroupRead = 0x20;    // 040

    public const int GroupWrite = 0x10;   // 020

    public const int GroupExecute = 0x8;  // 010

    public const int OtherRead = 0x4;     // 04

    public const int OtherWrite = 0x2;    // 02

    public const int OtherExecute = 0x1;  // 01

    /// <summary>All the bits chmod changes: the nine read, write and execute bits and the three special ones.</summary>
    public const int All = 0xFFF;

    /// <summary>
    /// Reads the mode column of ls ("-rwxr-xr-x", "drwxrwsr-t", also with the "." or "+" that marks an SELinux context or an
    /// ACL after it). Returns null when the text is not a mode, so that a listing in a format nobody expected does not
    /// show wrong permissions.
    /// </summary>
    public static int? ParseMode(string mode)
    {
        if (mode == null || mode.Length < 10)
        {
            return null;
        }
        int value = 0;
        for (int triplet = 0; triplet < 3; triplet++)
        {
            int shift = 6 - 3 * triplet;
            char read = mode[1 + 3 * triplet];
            char write = mode[2 + 3 * triplet];
            char execute = mode[3 + 3 * triplet];
            if (read == 'r')
            {
                value |= 4 << shift;
            }
            else if (read != '-')
            {
                return null;
            }
            if (write == 'w')
            {
                value |= 2 << shift;
            }
            else if (write != '-')
            {
                return null;
            }
            // The execute position also tells the special bit: s for setuid and setgid, t for sticky; the capital letter
            // means the special bit is set without the execute bit.
            char specialLower = triplet == 2 ? 't' : 's';
            int special = triplet == 0 ? SetUid : triplet == 1 ? SetGid : Sticky;
            if (execute == 'x')
            {
                value |= 1 << shift;
            }
            else if (execute == specialLower)
            {
                value |= (1 << shift) | special;
            }
            else if (execute == char.ToUpperInvariant(specialLower))
            {
                value |= special;
            }
            else if (execute != '-')
            {
                return null;
            }
        }
        return value;
    }

    /// <summary>The nine-character form of a mode, as ls prints it after the file type: "rwxr-xr-x".</summary>
    public static string ToSymbolic(int mode)
    {
        char[] text = new char[9];
        for (int triplet = 0; triplet < 3; triplet++)
        {
            int shift = 6 - 3 * triplet;
            text[3 * triplet] = (mode & (4 << shift)) != 0 ? 'r' : '-';
            text[3 * triplet + 1] = (mode & (2 << shift)) != 0 ? 'w' : '-';
            bool execute = (mode & (1 << shift)) != 0;
            int special = triplet == 0 ? SetUid : triplet == 1 ? SetGid : Sticky;
            char specialLower = triplet == 2 ? 't' : 's';
            if ((mode & special) != 0)
            {
                text[3 * triplet + 2] = execute ? specialLower : char.ToUpperInvariant(specialLower);
            }
            else
            {
                text[3 * triplet + 2] = execute ? 'x' : '-';
            }
        }
        return new string(text);
    }

    /// <summary>The octal form of a mode: three digits, four when a special bit is set ("755", "4755").</summary>
    public static string ToOctal(int mode)
    {
        mode &= All;
        return Convert.ToString(mode, 8).PadLeft((mode & (SetUid | SetGid | Sticky)) != 0 ? 4 : 3, '0');
    }

    /// <summary>"rwxr-xr-x (755)".</summary>
    public static string Describe(int mode)
    {
        return ToSymbolic(mode) + " (" + ToOctal(mode) + ")";
    }

    /// <summary>
    /// Classifies the mode column of ls ("-rwxr-xr-x") for the colour the file list draws a name in. The most telling
    /// property wins: a world-writable entry, then setuid or setgid, then an executable file, then a read-only entry.
    /// Symbolic links are never highlighted (ls always shows lrwxrwxrwx for them), neither is a world-writable folder with
    /// the sticky bit like /tmp, where that is how it is meant to be.
    /// </summary>
    public static PermissionHighlight Classify(string lsMode)
    {
        int? parsed = ParseMode(lsMode);
        if (!parsed.HasValue || lsMode[0] == 'l')
        {
            return PermissionHighlight.None;
        }
        int mode = parsed.Value;
        bool directory = lsMode[0] == 'd';
        if ((mode & OtherWrite) != 0 && !(directory && (mode & Sticky) != 0))
        {
            return PermissionHighlight.WorldWritable;
        }
        if ((mode & (SetUid | SetGid)) != 0)
        {
            return PermissionHighlight.SetId;
        }
        if (!directory && (mode & (OwnerExecute | GroupExecute | OtherExecute)) != 0)
        {
            return PermissionHighlight.Executable;
        }
        if ((mode & (OwnerWrite | GroupWrite | OtherWrite)) == 0)
        {
            return PermissionHighlight.ReadOnly;
        }
        return PermissionHighlight.None;
    }

    /// <summary>The short reason of the colour, for the tooltip; null for an entry that is not highlighted.</summary>
    public static string DescribeHighlight(PermissionHighlight highlight)
    {
        switch (highlight)
        {
            case PermissionHighlight.WorldWritable: return "writable by everyone";
            case PermissionHighlight.SetId: return "setuid or setgid";
            case PermissionHighlight.Executable: return "executable";
            case PermissionHighlight.ReadOnly: return "read-only";
            default: return null;
        }
    }

    /// <summary>Reads one to four octal digits ("7", "644", "1777"); false for anything else.</summary>
    public static bool TryParseOctal(string text, out int mode)
    {
        mode = 0;
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 4)
        {
            return false;
        }
        int value = 0;
        foreach (char digit in text)
        {
            if (digit < '0' || digit > '7')
            {
                return false;
            }
            value = value * 8 + (digit - '0');
        }
        mode = value;
        return true;
    }
}

/// <summary>
/// The permission bits to switch on and off. Only the bits named here are touched, so a change leaves every other bit of
/// each file as it is; <see cref="ToChmodMode"/> turns it into a symbolic chmod mode.
/// </summary>
public sealed class RemotePermissionChange
{
    public int SetBits;

    public int ClearBits;

    public bool IsEmpty => ((SetBits | ClearBits) & RemotePermissions.All) == 0;

    /// <summary>
    /// The symbolic mode for chmod, "u+rw,u-x,g-w,a+t" (or null for an empty change). Symbolic rather than octal, because a
    /// numeric mode would also reset the bits that were not changed, and with a number chmod treats the setuid and setgid
    /// bits of folders differently on GNU and on BusyBox. "a" stands before the sticky bit, which "-t" alone would make an
    /// option.
    /// </summary>
    public string ToChmodMode()
    {
        List<string> clauses = new List<string>();
        AddClauses(clauses, 'u', RemotePermissions.OwnerRead, RemotePermissions.OwnerWrite, RemotePermissions.OwnerExecute, RemotePermissions.SetUid);
        AddClauses(clauses, 'g', RemotePermissions.GroupRead, RemotePermissions.GroupWrite, RemotePermissions.GroupExecute, RemotePermissions.SetGid);
        AddClauses(clauses, 'o', RemotePermissions.OtherRead, RemotePermissions.OtherWrite, RemotePermissions.OtherExecute, 0);
        if ((SetBits & RemotePermissions.Sticky) != 0)
        {
            clauses.Add("a+t");
        }
        if ((ClearBits & ~SetBits & RemotePermissions.Sticky) != 0)
        {
            clauses.Add("a-t");
        }
        return clauses.Count == 0 ? null : string.Join(",", clauses);
    }

    private void AddClauses(List<string> clauses, char who, int read, int write, int execute, int special)
    {
        string set = Letters(SetBits, read, write, execute, special);
        string clear = Letters(ClearBits & ~SetBits, read, write, execute, special);
        if (set.Length != 0)
        {
            clauses.Add(who + "+" + set);
        }
        if (clear.Length != 0)
        {
            clauses.Add(who + "-" + clear);
        }
    }

    private static string Letters(int bits, int read, int write, int execute, int special)
    {
        return ((bits & read) != 0 ? "r" : "") + ((bits & write) != 0 ? "w" : "") + ((bits & execute) != 0 ? "x" : "") + (special != 0 && (bits & special) != 0 ? "s" : "");
    }
}

/// <summary>Why the file list draws a name in a colour of its own; see <see cref="RemotePermissions.Classify"/>.</summary>
public enum PermissionHighlight
{
    None,
    ReadOnly,
    Executable,
    SetId,
    WorldWritable
}
