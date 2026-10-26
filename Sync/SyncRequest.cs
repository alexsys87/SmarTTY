using SmarTTY.Ssh.QuickSync;
using System.Collections.Generic;

namespace SmarTTY.Sync;

/// <summary>
/// Builds a <see cref="SyncSettings"/> for <see cref="QuickSyncRunner"/> out of the plain values the synchronization
/// window collects. Kept out of the WPF window so the mapping from the checkboxes and the exclusion masks to the
/// engine settings can be checked without a user interface.
/// </summary>
public static class SyncRequest
{
    /// <summary>
    /// Assembles the settings of one synchronization. <paramref name="excludeMasks"/> is the raw "Exclude" text: masks
    /// separated by semicolons, an empty entry is ignored and a leading "-" on a mask is accepted like the engine does.
    /// </summary>
    public static SyncSettings Create(bool upload, string localPath, string remotePath, string excludeMasks, bool deleteExtraneous, bool skipSymlinks, bool skipHardlinks, QuickSyncOverwriteTrigger overwriteTrigger)
    {
        // The engine matches names case-insensitively (it is built for the Windows-side local file system), so the
        // masks are compiled the same way and a "*.LOG" mask also excludes "app.log".
        QuickSyncFilter[] filters = QuickSyncFilter.FromExclusions(SplitMasks(excludeMasks), ignoreCase: true);
        SyncOptions options = SyncOptions.None;
        if (deleteExtraneous)
        {
            options |= SyncOptions.DeleteExtraneous;
        }
        if (skipSymlinks)
        {
            options |= SyncOptions.SkipSymlinks;
        }
        if (skipHardlinks)
        {
            options |= SyncOptions.SkipHardlinks;
        }
        return new SyncSettings
        {
            Direction = upload ? SyncDirection.Upload : SyncDirection.Download,
            LocalPath = localPath,
            RemotePath = remotePath,
            DirectoryFilters = filters,
            FileFilters = filters,
            Options = options,
            QuickSyncOverwriteTrigger = overwriteTrigger
        };
    }

    /// <summary>
    /// Splits the "Exclude" text into masks. Returns null when nothing was entered, which the engine reads as
    /// "include everything".
    /// </summary>
    public static string[] SplitMasks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        List<string> masks = new List<string>();
        foreach (string mask in text.Split(';'))
        {
            string trimmed = mask.Trim();
            if (trimmed.Length != 0)
            {
                masks.Add(trimmed);
            }
        }
        return masks.Count == 0 ? null : masks.ToArray();
    }
}
