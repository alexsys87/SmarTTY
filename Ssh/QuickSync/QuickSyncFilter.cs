using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SmarTTY.Ssh.QuickSync;

public struct QuickSyncFilter
{

    public readonly Regex Regex;

    public readonly QuickSyncAction Action;

    private readonly Regex _fileNameRegex;

    private readonly bool _matchesAll;

    public static readonly QuickSyncFilter All = new QuickSyncFilter(new Regex(".*"), QuickSyncAction.Include);

    public static readonly QuickSyncFilter[] AllArray = new QuickSyncFilter[1] { All };

    public override string ToString()
    {
        if (Action == QuickSyncAction.Include)
        {
            return Regex.ToString();
        }
        return "-" + Regex;
    }

    public static QuickSyncFilter FromRegex(string maskText, bool ignoreCase)
    {
        if (maskText == null)
        {
            throw new ArgumentException("maskText");
        }
        QuickSyncAction quickSyncAction = QuickSyncAction.Include;
        if (maskText.StartsWith("-"))
        {
            quickSyncAction = QuickSyncAction.Exclude;
            maskText = maskText.Substring(1);
        }
        return new QuickSyncFilter(new Regex(maskText, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), quickSyncAction);
    }

    public static QuickSyncFilter FromWildcard(string maskText, bool ignoreCase)
    {
        if (maskText == null)
        {
            throw new ArgumentException("maskText");
        }
        MaskSpec maskSpec = WildcardParser.ParseMask(maskText.Replace('\\', '/'), ignoreCase);
        return new QuickSyncFilter(maskSpec.Regex, (!maskSpec.IsInclude) ? QuickSyncAction.Exclude : QuickSyncAction.Include);
    }

    public QuickSyncFilter(Regex regex, QuickSyncAction quickSyncAction)
    {
        this.Regex = regex;
        Action = quickSyncAction;
        string pattern = regex.ToString();
        if (pattern.StartsWith("(^|.*/)") && pattern.IndexOf('/', "(^|.*/)".Length) == -1)
        {
            _fileNameRegex = new Regex("^" + pattern.Substring("(^|.*/)".Length), regex.Options);
        }
        else
        {
            _fileNameRegex = null;
        }
        _matchesAll = pattern == ".*" || pattern == "^.*$";
    }

    public static QuickSyncFilter[] FromPaths(IEnumerable<string> paths, bool ignoreCase)
    {
        HashSet<string> parentDirs = new HashSet<string>();
        HashSet<string> targetPaths = new HashSet<string>();
        HashSet<string> dirWildcards = new HashSet<string>();
        bool hasRootWildcard = false;
        foreach (string path in paths)
        {
            if (!string.IsNullOrEmpty(path))
            {
                string normPath = path.Replace('\\', '/');
                if (normPath == "*")
                {
                    hasRootWildcard = true;
                }
                else if (normPath.EndsWith("/*"))
                {
                    dirWildcards.Add(normPath.Substring(0, normPath.Length - 2));
                }
                else
                {
                    targetPaths.Add(normPath);
                }
                string parentDir = RemotePathUtils.GetDirectoryName(normPath);
                while (!string.IsNullOrEmpty(parentDir))
                {
                    parentDirs.Add(parentDir);
                    parentDir = RemotePathUtils.GetDirectoryName(parentDir);
                }
            }
        }
        List<QuickSyncFilter> filters = new List<QuickSyncFilter>();
        if (parentDirs.Count > 0)
        {
            filters.Add(new QuickSyncFilter(new Regex("^(" + string.Join("|", parentDirs.ArraySelect(Regex.Escape)) + ")$"), QuickSyncAction.Include));
        }
        if (targetPaths.Count > 0)
        {
            filters.Add(new QuickSyncFilter(new Regex("^(" + string.Join("|", targetPaths.ArraySelect(Regex.Escape)) + ")($|/.*)"), QuickSyncAction.Include));
        }
        if (dirWildcards.Count > 0)
        {
            filters.Add(new QuickSyncFilter(new Regex("^(" + string.Join("|", dirWildcards.ArraySelect(Regex.Escape)) + ")/\\.$"), QuickSyncAction.Include));
        }
        if (hasRootWildcard)
        {
            filters.Add(new QuickSyncFilter(new Regex("^\\.$"), QuickSyncAction.Include));
        }
        return filters.ToArray();
    }

    public static QuickSyncFilter[] FromMaskList(IEnumerable<string> masks, bool ignoreCase)
    {
        if (masks == null)
        {
            return null;
        }
        List<string> groupMasks = new List<string>();
        bool isIncludeGroup = false;
        List<QuickSyncFilter> items = new List<QuickSyncFilter>();
        foreach (string mask in masks)
        {
            bool isInclude = true;
            string cleanMask = mask;
            if (mask.StartsWith("-"))
            {
                isInclude = false;
                cleanMask = mask.Substring(1);
            }
            if (groupMasks.Count > 0 && isInclude != isIncludeGroup)
            {
                items.Add(QuickSyncFilter.FromWildcard((isIncludeGroup ? "" : "-") + string.Join(";", groupMasks.ToArray()), ignoreCase));
                groupMasks.Clear();
            }
            groupMasks.Add(cleanMask);
            isIncludeGroup = isInclude;
        }
        if (groupMasks.Count > 0)
        {
            items.Add(QuickSyncFilter.FromWildcard((isIncludeGroup ? "" : "-") + string.Join(";", groupMasks.ToArray()), ignoreCase));
        }
        return items.ToArray();
    }

    public bool IsMatch(string path)
    {
        if (this.Regex == null || path == null)
        {
            return false;
        }
        if (_matchesAll)
        {
            return true;
        }
        if (_fileNameRegex != null)
        {
            int slashIndex = path.LastIndexOf('/');
            if (slashIndex != -1)
            {
                path = path.Substring(slashIndex + 1);
            }
            return _fileNameRegex.IsMatch(path);
        }
        return this.Regex.IsMatch(path);
    }

    public static QuickSyncFilter[] FromExclusions(string[] exclusions, bool ignoreCase)
    {
        if (exclusions == null)
        {
            return null;
        }
        return FromMaskList(exclusions.Select((string pattern) => "-" + pattern).AppendSlow("*"), ignoreCase);
    }
}
