using System;
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

    public string Text;
}

/// <summary>Encrypts the text of the drafts at rest (see <see cref="DpapiDraftProtector"/>).</summary>
public interface IDraftProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Drafts of the files that are being edited, one file per remote file. The editor writes a draft while the text has
/// unsaved changes, so that they survive a lost connection or a closed program, and removes it once the text is saved or
/// the user chose to discard it. A file is replaced as a whole (written next to it, then swapped in with File.Replace),
/// so a program that ends in the middle of a write, or a swap that fails, leaves the previous draft, not half of a new
/// one and not none. With a <see cref="IDraftProtector"/> the text is stored encrypted (format 2); drafts written without
/// one (format 1, plain text) are still read.
/// </summary>
public sealed class EditorDraftStore
{
    private const string Signature = "SmarTTY-draft 1";

    private const string ProtectedSignature = "SmarTTY-draft 2";

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
        StringBuilder text = new StringBuilder();
        text.Append(Signature).Append('\n');
        text.Append("path: ").Append(Convert.ToBase64String(new UTF8Encoding(false).GetBytes(draft.RemotePath ?? ""))).Append('\n');
        text.Append("saved: ").Append(draft.SavedUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("base: ").Append(draft.BaseHash ?? "").Append('\n');
        text.Append("new: ").Append(draft.IsNewFile ? "1" : "0").Append('\n');
        // An empty line ends the header; the text after it is kept byte for byte (it may start with an empty line).
        text.Append('\n');
        text.Append(draft.Text ?? "");
        string content = text.ToString();
        if (_protector != null)
        {
            // A failure to protect is an error, not a reason to write the text in the clear: the caller tries again.
            content = ProtectedSignature + "\n" + Convert.ToBase64String(_protector.Protect(new UTF8Encoding(false).GetBytes(content))) + "\n";
        }
        string target = PathFor(key);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
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
            string content = File.ReadAllText(path, new UTF8Encoding(false));
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
            if (header.Length < 5 || header[0] != Signature)
            {
                return null;
            }
            EditorDraft draft = new EditorDraft
            {
                RemotePath = Encoding.UTF8.GetString(Convert.FromBase64String(Field(header[1], "path: "))),
                SavedUtc = new DateTime(long.Parse(Field(header[2], "saved: "), CultureInfo.InvariantCulture), DateTimeKind.Utc),
                BaseHash = Field(header[3], "base: "),
                IsNewFile = Field(header[4], "new: ") == "1",
                Text = content.Substring(headerEnd + 2)
            };
            return draft;
        }
        catch
        {
            return null;
        }
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

    /// <summary>Removes the drafts that were last written longer ago than <paramref name="age"/>; returns how many.</summary>
    public int DeleteOlderThan(TimeSpan age)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(_directory))
            {
                return 0;
            }
            DateTime limit = DateTime.UtcNow - age;
            foreach (string file in Directory.GetFiles(_directory, "*" + Extension))
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
        }
        catch
        {
        }
        return removed;
    }
}
