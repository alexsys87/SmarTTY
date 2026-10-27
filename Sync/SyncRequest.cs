using SmarTTY.Ssh.QuickSync;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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
    public static SyncSettings Create(bool upload, string localPath, string remotePath, string excludeMasks, bool deleteExtraneous, bool skipSymlinks, bool skipHardlinks, QuickSyncOverwriteTrigger overwriteTrigger, bool excludeIsRegex = false, string[] selectionPaths = null)
    {
        QuickSyncFilter[] fileFilters = BuildExcludeFilters(excludeMasks, excludeIsRegex);
        QuickSyncFilter[] directoryFilters = BuildDirectoryFilters(excludeMasks, excludeIsRegex, selectionPaths);
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
            DirectoryFilters = directoryFilters,
            FileFilters = fileFilters,
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
        QuickSyncFilter[] excludes = BuildExcludeFiltersWithoutTail(excludeText, isRegex);
        if (excludes == null)
        {
            return null;
        }
        // The engine takes the first matching filter and treats "no match" as excluded, so the include-all tail keeps
        // every name the exclusion does not match (this mirrors QuickSyncFilter.FromExclusions).
        return excludes.AppendSlow(QuickSyncFilter.All).ToArray();
    }

    /// <summary>
    /// The "Exclude" filters without the include-all tail, so they can be combined with a folder selection (the tail
    /// would otherwise defeat it). Returns null when nothing was entered.
    /// </summary>
    private static QuickSyncFilter[] BuildExcludeFiltersWithoutTail(string excludeText, bool isRegex)
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
            // tail is added by BuildExcludeFilters; the engine takes the first matching filter and treats "no match"
            // as excluded, so a lone exclude filter would drop every file (this mirrors QuickSyncFilter.FromExclusions).
            return new QuickSyncFilter[1]
            {
                QuickSyncFilter.FromRegex("-" + pattern, ignoreCase: true)
            };
        }
        // The engine matches names case-insensitively (it is built for the Windows-side local file system), so the
        // masks are compiled the same way and a "*.LOG" mask also excludes "app.log".
        string[] masks = SplitMasks(excludeText);
        return (masks == null) ? null : QuickSyncFilter.FromMaskList(masks.Select((string mask) => "-" + mask), ignoreCase: true);
    }

    /// <summary>
    /// Builds the directory filters: the exclude masks, narrowed to the selected folders when a selection is given.
    /// A selection is the names of the top-level folders of the synchronization root that must be synchronized; the
    /// other folders are left out, and so are the loose files at the root (only a selected folder and its contents are
    /// copied). The engine matches files by name alone, so a folder selection can only be expressed on the directory
    /// filters; a file is copied when its folder is included and the file passes the exclude masks. Returns null when
    /// there is nothing to filter, which the engine reads as "include everything".
    /// </summary>
    public static QuickSyncFilter[] BuildDirectoryFilters(string excludeText, bool isRegex, string[] selectionPaths)
    {
        string[] folders = CleanSelection(selectionPaths);
        if (folders == null)
        {
            return BuildExcludeFilters(excludeText, isRegex);
        }
        QuickSyncFilter[] excludes = BuildExcludeFiltersWithoutTail(excludeText, isRegex);
        List<QuickSyncFilter> filters = new List<QuickSyncFilter>();
        if (excludes != null)
        {
            filters.AddRange(excludes);
        }
        foreach (string folder in folders)
        {
            // The subtree of the folder: the folder itself, the folder as a visited directory ("folder/."), and
            // everything below it. The root (".") is deliberately not included, so the loose files at the root are
            // left out; the engine still descends into the selected folders.
            filters.Add(QuickSyncFilter.FromRegex("^" + Regex.Escape(folder) + "($|/)", ignoreCase: true));
        }
        return filters.ToArray();
    }

    /// <summary>
    /// Normalizes a folder selection: trims the entries, uses forward slashes, drops the empty ones and the duplicates,
    /// and refuses a path that climbs out of the synchronization root. Returns null when nothing is left, which means
    /// "synchronize everything".
    /// </summary>
    public static string[] CleanSelection(string[] selectionPaths)
    {
        if (selectionPaths == null)
        {
            return null;
        }
        List<string> folders = new List<string>();
        foreach (string path in selectionPaths)
        {
            string folder = path?.Trim().Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(folder) || folder == "." || folder.Split('/').Any((string segment) => segment == ".."))
            {
                continue;
            }
            if (!folders.Any((string existing) => string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase)))
            {
                folders.Add(folder);
            }
        }
        return folders.Count == 0 ? null : folders.ToArray();
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
