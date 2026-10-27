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
    /// When <paramref name="excludeIsRegex"/> is set, the whole text is one regular expression instead (see
    /// <see cref="BuildExcludeFilters"/>); an invalid pattern throws <see cref="ArgumentException"/>.
    /// </summary>
    public static SyncSettings Create(bool upload, string localPath, string remotePath, string excludeMasks, bool deleteExtraneous, bool skipSymlinks, bool skipHardlinks, QuickSyncOverwriteTrigger overwriteTrigger, bool excludeIsRegex = false)
    {
        QuickSyncFilter[] filters = BuildExcludeFilters(excludeMasks, excludeIsRegex);
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
    /// Builds the file and directory filters of the "Exclude" box. In mask mode the entries are wildcards joined by
    /// semicolons; in regular-expression mode the whole text is one pattern and every name that matches it is left
    /// out. Returns null when nothing was entered, which the engine reads as "include everything". An invalid pattern
    /// makes <see cref="QuickSyncFilter.FromRegex"/> throw <see cref="ArgumentException"/>.
    /// </summary>
    public static QuickSyncFilter[] BuildExcludeFilters(string excludeText, bool isRegex)
    {
        if (isRegex)
        {
            string pattern = excludeText?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                return null;
            }
            // "FromRegex" treats a leading "-" as "exclude the matches", which is what the box means; the ignore-case
            // flag matches the mask mode (and the engine, which matches names case-insensitively). The include-all
            // tail is required: the engine takes the first matching filter and treats "no match" as excluded, so a
            // lone exclude filter would drop every file (this mirrors QuickSyncFilter.FromExclusions).
            return new QuickSyncFilter[2]
            {
                QuickSyncFilter.FromRegex("-" + pattern, ignoreCase: true),
                QuickSyncFilter.All
            };
        }
        // The engine matches names case-insensitively (it is built for the Windows-side local file system), so the
        // masks are compiled the same way and a "*.LOG" mask also excludes "app.log".
        return QuickSyncFilter.FromExclusions(SplitMasks(excludeText), ignoreCase: true);
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
