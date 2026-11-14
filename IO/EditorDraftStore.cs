using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmarTTY.IO;

/// <summary>The unsaved text of a remote file kept on this computer (see <see cref="EditorDraftStore"/>).</summary>
public sealed class EditorDraft
{
    public string RemotePath;

    /// <summary>When the draft was written (UTC).</summary>
    public DateTime SavedUtc;

    /// <summary>
    /// <see cref="EditorDraftStore.HashText"/> of the text the file had on the server when it was opened, so that a
    /// restore can tell that the file has been changed by someone else since.
    /// </summary>
    public string BaseHash;

    /// <summary>The file did not exist when the editor was opened.</summary>
    public bool IsNewFile;

    /// <summary>The unsaved text (formats 1 and 2).</summary>
    public string Text;

    /// <summary>
    /// The unsaved bytes of the hex view (format 3), or null for a text draft. An empty file is an empty array,
    /// not null, so that the two kinds of drafts stay apart.
    /// </summary>
    public byte[] Bytes;
}

/// <summary>Encrypts the text of the drafts at rest (see <see cref="DpapiDraftProtector"/>).</summary>
public interface IDraftProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Decides when the editor takes a draft of a text that has changed. Taking one reads the whole text (or the bytes of the hex
/// view) on the UI thread, which is a pause the user can feel for a file of tens of megabytes. A small text is taken at every
/// tick of the timer. A large one is taken at most once a minute, and only when it has stood still since the previous tick (the
/// user is not typing right now); typing without a pause for minutes does not postpone it for ever.
/// </summary>
public sealed class DraftSchedule
{
    /// <summary>From this size on a text counts as large (the size at which the editor warns before opening a file).</summary>
    public const long LargeBytes = 8L * 1024 * 1024;

    public static readonly TimeSpan LargeMinInterval = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan LargeMaxWait = TimeSpan.FromMinutes(3);

    private object _previousVersion;

    private DateTime _lastCaptureUtc = DateTime.MinValue;

    /// <summary>
    /// Whether to take the draft now. Call it at every tick for a text that has changed since the last draft;
    /// <paramref name="size"/> is its approximate size in characters or bytes.
    /// </summary>
    public bool ShouldCapture(object version, long size, DateTime nowUtc)
    {
        bool stable = ReferenceEquals(version, _previousVersion);
        _previousVersion = version;
        if (size < LargeBytes)
        {
            return true;
        }
        if (_lastCaptureUtc == DateTime.MinValue)
        {
            // No draft yet: the time counts from the first change that is seen, not from the start of time.
            _lastCaptureUtc = nowUtc;
        }
        TimeSpan sinceLast = nowUtc - _lastCaptureUtc;
        if (sinceLast < LargeMinInterval)
        {
            return false;
        }
        return stable || sinceLast >= LargeMaxWait;
    }

    /// <summary>Records that a draft was taken.</summary>
    public void Captured(DateTime nowUtc)
    {
        _lastCaptureUtc = nowUtc;
    }
}

/// <summary>
/// Drafts of the files that are being edited, one file per remote file. The editor writes a draft while the text has
/// unsaved changes, so that they survive a lost connection or a closed program, and removes it once the text is saved or
/// the user chose to discard it. A file is replaced as a whole (written next to it, then swapped in with File.Replace),
/// so a program that ends in the middle of a write, or a swap that fails, leaves the previous draft, not half of a new
/// one and not none. With a <see cref="IDraftProtector"/> a draft is stored encrypted as one binary block (format 4: the
/// text as UTF-8, the hex view as its raw bytes, so a draft is not made larger by base64 once and again). Drafts written
/// without a protector are plain text (format 1) or, for the hex view, base64 (format 3); the encrypted base64 files of the
/// earlier version (format 2) are still read.
/// </summary>
public sealed class EditorDraftStore
{
    private const string Signature = "SmarTTY-draft 1";

    private const string ProtectedSignature = "SmarTTY-draft 2";

    private const string HexSignature = "SmarTTY-draft 3";

    // An encrypted draft: this line, then the block that the protector made of the header and the payload (see Save).
    private const string BinarySignature = "SmarTTY-draft 4";

    // The temporary file of a write that was cut short by a crash is "<key>.draft.<guid>.tmp"; a write takes a moment, so a
    // temporary file this old is dead.
    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromHours(1);

    private const string Extension = ".draft";

    private readonly string _directory;

    private readonly IDraftProtector _protector;

    public EditorDraftStore(string directory, IDraftProtector protector = null)
    {
        _directory = directory;
        _protector = protector;
    }

    /// <summary>The name of a draft: the same session and remote path always give the same key.</summary>
    public static string KeyFor(string sessionKey, string remotePath)
    {
        return HashText((sessionKey ?? "") + "\n" + (remotePath ?? "")).Substring(0, 32);
    }

    public static string HashText(string text)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text ?? ""));
        StringBuilder builder = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private string PathFor(string key)
    {
        return Path.Combine(_directory, key + Extension);
    }

    public void Save(string key, EditorDraft draft)
    {
        Directory.CreateDirectory(_directory);
        bool hex = draft.Bytes != null;
        UTF8Encoding utf8 = new UTF8Encoding(false);
        StringBuilder header = new StringBuilder();
        header.Append(hex ? HexSignature : Signature).Append('\n');
        header.Append("path: ").Append(Convert.ToBase64String(utf8.GetBytes(draft.RemotePath ?? ""))).Append('\n');
        header.Append("saved: ").Append(draft.SavedUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        header.Append("base: ").Append(draft.BaseHash ?? "").Append('\n');
        header.Append("new: ").Append(draft.IsNewFile ? "1" : "0").Append('\n');
        // An empty line ends the header; the text after it is kept byte for byte (it may start with an empty line).
        header.Append('\n');
        byte[] content;
        if (_protector != null)
        {
            // The header and the payload (the UTF-8 text, or the raw bytes of the hex view) in one array that the protector
            // encrypts as it is: no base64 before it and none after it. A failure to protect is an error, not a reason to write
            // the text in the clear: the caller tries again.
            byte[] headerBytes = utf8.GetBytes(header.ToString());
            string draftText = draft.Text ?? "";
            int payloadLength = hex ? draft.Bytes.Length : utf8.GetByteCount(draftText);
            byte[] plain = new byte[headerBytes.Length + payloadLength];
            Buffer.BlockCopy(headerBytes, 0, plain, 0, headerBytes.Length);
            if (hex)
            {
                Buffer.BlockCopy(draft.Bytes, 0, plain, headerBytes.Length, payloadLength);
            }
            else
            {
                utf8.GetBytes(draftText, 0, draftText.Length, plain, headerBytes.Length);
            }
            byte[] protectedBytes = _protector.Protect(plain);
            byte[] signature = Encoding.ASCII.GetBytes(BinarySignature + "\n");
            content = new byte[signature.Length + protectedBytes.Length];
            Buffer.BlockCopy(signature, 0, content, 0, signature.Length);
            Buffer.BlockCopy(protectedBytes, 0, content, signature.Length, protectedBytes.Length);
        }
        else
        {
            header.Append(hex ? Convert.ToBase64String(draft.Bytes) : (draft.Text ?? ""));
            content = utf8.GetBytes(header.ToString());
        }
        string target = PathFor(key);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            ReplaceFile(temporary, target);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Puts the finished temporary file in place of the draft without ever leaving a moment with no draft: File.Replace
    /// swaps them in one step. A virus scanner or an indexer may hold either file for a moment, so a failure is tried
    /// again a few times; if it stays, the old draft is still there.
    /// </summary>
    private static void ReplaceFile(string temporary, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(target))
                {
                    try
                    {
                        File.Replace(temporary, target, null);
                        return;
                    }
                    catch (FileNotFoundException) when (!File.Exists(target))
                    {
                        // The draft was removed between the check and the swap: there is nothing to replace.
                    }
                }
                File.Move(temporary, target);
                return;
            }
            catch (Exception exception) when (attempt < 5 && (exception is IOException || exception is UnauthorizedAccessException))
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>The draft, or null if there is none or it cannot be read (a damaged draft is not worth an error).</summary>
    public EditorDraft TryLoad(string key)
    {
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }
            byte[] fileBytes = File.ReadAllBytes(path);
            byte[] binarySignature = Encoding.ASCII.GetBytes(BinarySignature + "\n");
            if (StartsWith(fileBytes, binarySignature))
            {
                return _protector == null ? null : ParseBinary(_protector.Unprotect(Slice(fileBytes, binarySignature.Length, fileBytes.Length - binarySignature.Length)));
            }
            string content = new UTF8Encoding(false).GetString(fileBytes);
            if (content.StartsWith(ProtectedSignature + "\n", StringComparison.Ordinal))
            {
                if (_protector == null)
                {
                    return null;
                }
                string body = content.Substring(ProtectedSignature.Length + 1).Trim();
                content = new UTF8Encoding(false).GetString(_protector.Unprotect(Convert.FromBase64String(body)));
            }
            int headerEnd = content.IndexOf("\n\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                return null;
            }
            string[] header = content.Substring(0, headerEnd).Split('\n');
            bool hex = header.Length >= 5 && header[0] == HexSignature;
            if (header.Length < 5 || (!hex && header[0] != Signature))
            {
                return null;
            }
            string payload = content.Substring(headerEnd + 2);
            EditorDraft draft = new EditorDraft
            {
                RemotePath = Encoding.UTF8.GetString(Convert.FromBase64String(Field(header[1], "path: "))),
                SavedUtc = new DateTime(long.Parse(Field(header[2], "saved: "), CultureInfo.InvariantCulture), DateTimeKind.Utc),
                BaseHash = Field(header[3], "base: "),
                IsNewFile = Field(header[4], "new: ") == "1",
                Text = hex ? null : payload,
                Bytes = hex ? Convert.FromBase64String(payload.Trim()) : null
            };
            return draft;
        }
        catch
        {
            return null;
        }
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] Slice(byte[] data, int offset, int count)
    {
        byte[] part = new byte[count];
        Buffer.BlockCopy(data, offset, part, 0, count);
        return part;
    }

    /// <summary>Reads what <see cref="Save"/> gave the protector: the header, an empty line, then the payload as it is.</summary>
    private static EditorDraft ParseBinary(byte[] plain)
    {
        int headerEnd = -1;
        for (int i = 0; i + 1 < plain.Length; i++)
        {
            if (plain[i] == (byte)'\n' && plain[i + 1] == (byte)'\n')
            {
                headerEnd = i;
                break;
            }
        }
        if (headerEnd < 0)
        {
            return null;
        }
        UTF8Encoding utf8 = new UTF8Encoding(false);
        string[] header = utf8.GetString(plain, 0, headerEnd).Split('\n');
        bool hex = header.Length >= 5 && header[0] == HexSignature;
        if (header.Length < 5 || (!hex && header[0] != Signature))
        {
            return null;
        }
        int payloadStart = headerEnd + 2;
        int payloadLength = plain.Length - payloadStart;
        return new EditorDraft
        {
            RemotePath = utf8.GetString(Convert.FromBase64String(Field(header[1], "path: "))),
            SavedUtc = new DateTime(long.Parse(Field(header[2], "saved: "), CultureInfo.InvariantCulture), DateTimeKind.Utc),
            BaseHash = Field(header[3], "base: "),
            IsNewFile = Field(header[4], "new: ") == "1",
            Text = hex ? null : utf8.GetString(plain, payloadStart, payloadLength),
            Bytes = hex ? Slice(plain, payloadStart, payloadLength) : null
        };
    }

    private static string Field(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new FormatException("Unexpected draft header line");
        }
        return line.Substring(prefix.Length);
    }

    public void Delete(string key)
    {
        try
        {
            string path = PathFor(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Removes the drafts that were last written longer ago than <paramref name="age"/>, and the temporary files that a write
    /// cut short by a crash left behind ("*.draft.&lt;guid&gt;.tmp", which are no drafts and so were never cleaned); returns how
    /// many files were removed.
    /// </summary>
    public int DeleteOlderThan(TimeSpan age)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(_directory))
            {
                return 0;
            }
            DateTime now = DateTime.UtcNow;
            removed += DeleteFilesOlderThan("*" + Extension, now - age);
            removed += DeleteFilesOlderThan("*" + Extension + ".*.tmp", now - (age < StaleTemporaryAge ? age : StaleTemporaryAge));
        }
        catch
        {
        }
        return removed;
    }

    private int DeleteFilesOlderThan(string pattern, DateTime limit)
    {
        int removed = 0;
        foreach (string file in Directory.GetFiles(_directory, pattern))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch
            {
            }
        }
        return removed;
    }

    /// <summary>A draft that <see cref="DeleteOlderThan"/> will remove soon.</summary>
    public sealed class ExpiringDraft
    {
        public string RemotePath;

        /// <summary>When the draft will be removed (UTC).</summary>
        public DateTime ExpiresUtc;
    }

    /// <summary>
    /// The drafts that are not yet old enough for <see cref="DeleteOlderThan"/>(<paramref name="age"/>) but will be within
    /// <paramref name="warnBefore"/>, soonest first, so that the user can be told before their unsaved changes go.
    /// </summary>
    public List<ExpiringDraft> FindExpiring(TimeSpan age, TimeSpan warnBefore)
    {
        List<ExpiringDraft> result = new List<ExpiringDraft>();
        try
        {
            if (!Directory.Exists(_directory))
            {
                return result;
            }
            DateTime now = DateTime.UtcNow;
            foreach (string file in Directory.GetFiles(_directory, "*" + Extension))
            {
                try
                {
                    DateTime expires = File.GetLastWriteTimeUtc(file) + age;
                    if (expires <= now || expires - now > warnBefore)
                    {
                        continue;
                    }
                    EditorDraft draft = TryLoad(Path.GetFileNameWithoutExtension(file));
                    if (draft != null)
                    {
                        result.Add(new ExpiringDraft { RemotePath = draft.RemotePath, ExpiresUtc = expires });
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        result.Sort((ExpiringDraft a, ExpiringDraft b) => a.ExpiresUtc.CompareTo(b.ExpiresUtc));
        return result;
    }

    /// <summary>The text that tells the user which drafts are about to be removed, or null when there are none.</summary>
    public static string DescribeExpiring(List<ExpiringDraft> drafts, DateTime nowUtc, int maxListed = 5)
    {
        if (drafts == null || drafts.Count == 0)
        {
            return null;
        }
        StringBuilder text = new StringBuilder();
        text.Append("SmarTTY keeps the unsaved changes of an edited file in a draft on this computer, and removes a draft that has not been written to for a long time. These drafts will be removed soon:\n\n");
        for (int i = 0; i < drafts.Count && i < maxListed; i++)
        {
            int days = (int)Math.Ceiling((drafts[i].ExpiresUtc - nowUtc).TotalDays);
            text.Append("  ").Append(drafts[i].RemotePath).Append(" (in ").Append(Math.Max(days, 1).ToString(CultureInfo.InvariantCulture)).Append(Math.Max(days, 1) == 1 ? " day)" : " days)").Append('\n');
        }
        if (drafts.Count > maxListed)
        {
            text.Append("  and ").Append((drafts.Count - maxListed).ToString(CultureInfo.InvariantCulture)).Append(" more\n");
        }
        text.Append("\nOpen such a file in the editor to restore its draft and save it to the server.");
        return text.ToString();
    }
}
