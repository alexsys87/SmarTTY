using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SmarTTY.CodeEditing;

/// <summary>
/// Gives every remote file editor its own key for the draft of its unsaved changes. Two editors of the same file in one session must not
/// share a draft: the save or the discard of one would remove the draft of the other, and neither would see the other's text. The first
/// editor of a file has the plain key, the next ones the key with a number; the number is free again when the editor is closed.
/// This holds across programs too: SmarTTY may be started more than once, so a key is also claimed with a lock file that is kept open
/// (and deleted by the system when it is closed, or when the program ends in any way) while the editor lives.
/// The code is that of <c>RemoteFileEditor</c>, moved here as it was; the folder of the lock files is a parameter.
/// </summary>
public sealed class DraftKeyClaims
{
    private readonly string _directory;

    // The value is the lock (null when the folder cannot be used: then only this program's own editors are told apart).
    private readonly Dictionary<string, FileStream> _claimed = new Dictionary<string, FileStream>();

    /// <param name="directory">The folder of the drafts, where the lock files are made.</param>
    public DraftKeyClaims(string directory)
    {
        _directory = directory;
    }

    /// <summary>Takes the first free key made of <paramref name="baseKey"/>; give it back with <see cref="Release"/>.</summary>
    public string Claim(string baseKey)
    {
        lock (_claimed)
        {
            string key = baseKey;
            for (int number = 1; ; number++)
            {
                key = number == 1 ? baseKey : baseKey + "-" + number.ToString(CultureInfo.InvariantCulture);
                if (_claimed.ContainsKey(key))
                {
                    continue;
                }
                FileStream claim = TryLock(key, out bool heldElsewhere);
                if (heldElsewhere && number < 1000)
                {
                    continue;
                }
                _claimed[key] = claim;
                return key;
            }
        }
    }

    /// <summary>
    /// Takes the lock file of the key. <paramref name="heldElsewhere"/> is true if another program has it; null with
    /// false means the lock could not be made at all (the folder is not writable), which is no reason not to edit.
    /// </summary>
    private FileStream TryLock(string key, out bool heldElsewhere)
    {
        heldElsewhere = false;
        try
        {
            Directory.CreateDirectory(_directory);
            return new FileStream(Path.Combine(_directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) == 32 || (exception.HResult & 0xFFFF) == 33)
        {
            // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: an editor of another program has the key.
            heldElsewhere = true;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Gives the key back: the next editor of the file may have it.</summary>
    public void Release(string key)
    {
        lock (_claimed)
        {
            if (_claimed.TryGetValue(key, out FileStream claim))
            {
                _claimed.Remove(key);
                try
                {
                    claim?.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
