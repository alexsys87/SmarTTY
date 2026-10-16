using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ManagedLibSSH2.QuickSync;

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

    public static QuickSyncFilter FromRegex(string text, bool flag)
    {
        if (text == null)
        {
            throw new ArgumentException("mask");
        }
        QuickSyncAction quickSyncAction = QuickSyncAction.Include;
        if (text.StartsWith("-"))
        {
            quickSyncAction = QuickSyncAction.Exclude;
            text = text.Substring(1);
        }
        return new QuickSyncFilter(new Regex(text, flag ? RegexOptions.IgnoreCase : RegexOptions.None), quickSyncAction);
    }

    public static QuickSyncFilter FromWildcard(string text, bool flag)
    {
        if (text == null)
        {
            throw new ArgumentException("mask");
        }
        MaskSpec maskSpec = WildcardParser.ParseMask(text.Replace('\\', '/'), flag);
        return new QuickSyncFilter(maskSpec.Regex, (!maskSpec.IsInclude) ? QuickSyncAction.Exclude : QuickSyncAction.Include);
    }

    public QuickSyncFilter(Regex regex, QuickSyncAction quickSyncAction)
    {
        this.Regex = regex;
        Action = quickSyncAction;
        string text = regex.ToString();
        if (text.StartsWith("(^|.*/)") && text.IndexOf('/', "(^|.*/)".Length) == -1)
        {
            _fileNameRegex = new Regex("^" + text.Substring("(^|.*/)".Length), regex.Options);
        }
        else
        {
            _fileNameRegex = null;
        }
        _matchesAll = text == ".*" || text == "^.*$";
    }

    public static QuickSyncFilter[] FromPaths(IEnumerable<string> items, bool flag2)
    {
        HashSet<string> hashSet = new HashSet<string>();
        HashSet<string> hashSet2 = new HashSet<string>();
        HashSet<string> hashSet3 = new HashSet<string>();
        bool flag = false;
        foreach (string item in items)
        {
            if (!string.IsNullOrEmpty(item))
            {
                string text = item.Replace('\\', '/');
                if (text == "*")
                {
                    flag = true;
                }
                else if (text.EndsWith("/*"))
                {
                    hashSet3.Add(text.Substring(0, text.Length - 2));
                }
                else
                {
                    hashSet2.Add(text);
                }
                string text2 = RemotePathUtils.GetDirectoryName(text);
                while (!string.IsNullOrEmpty(text2))
                {
                    hashSet.Add(text2);
                    text2 = RemotePathUtils.GetDirectoryName(text2);
                }
            }
        }
        List<QuickSyncFilter> list = new List<QuickSyncFilter>();
        if (hashSet.Count > 0)
        {
            list.Add(new QuickSyncFilter(new Regex("^(" + string.Join("|", hashSet.ArraySelect(Regex.Escape)) + ")$"), QuickSyncAction.Include));
        }
        if (hashSet2.Count > 0)
        {
            list.Add(new QuickSyncFilter(new Regex("^(" + string.Join("|", hashSet2.ArraySelect(Regex.Escape)) + ")($|/.*)"), QuickSyncAction.Include));
        }
        if (hashSet3.Count > 0)
        {
            list.Add(new QuickSyncFilter(new Regex("^(" + string.Join("|", hashSet3.ArraySelect(Regex.Escape)) + ")/\\.$"), QuickSyncAction.Include));
        }
        if (flag)
        {
            list.Add(new QuickSyncFilter(new Regex("^\\.$"), QuickSyncAction.Include));
        }
        return list.ToArray();
    }

    public static QuickSyncFilter[] FromMaskList(IEnumerable<string> items, bool flag3)
    {
        if (items == null)
        {
            return null;
        }
        List<string> list = new List<string>();
        bool flag = false;
        List<QuickSyncFilter> list2 = new List<QuickSyncFilter>();
        foreach (string item2 in items)
        {
            bool flag2 = true;
            string item = item2;
            if (item2.StartsWith("-"))
            {
                flag2 = false;
                item = item2.Substring(1);
            }
            if (list.Count > 0 && flag2 != flag)
            {
                list2.Add(QuickSyncFilter.FromWildcard((flag ? "" : "-") + string.Join(";", list.ToArray()), flag3));
                list.Clear();
            }
            list.Add(item);
            flag = flag2;
        }
        if (list.Count > 0)
        {
            list2.Add(QuickSyncFilter.FromWildcard((flag ? "" : "-") + string.Join(";", list.ToArray()), flag3));
        }
        return list2.ToArray();
    }

    public bool IsMatch(string text)
    {
        if (this.Regex == null || text == null)
        {
            return false;
        }
        if (_matchesAll)
        {
            return true;
        }
        if (_fileNameRegex != null)
        {
            int num = text.LastIndexOf('/');
            if (num != -1)
            {
                text = text.Substring(num + 1);
            }
            return _fileNameRegex.IsMatch(text);
        }
        return this.Regex.IsMatch(text);
    }

    public static QuickSyncFilter[] FromExclusions(string[] items, bool flag)
    {
        if (items == null)
        {
            return null;
        }
        return FromMaskList(items.Select((string text) => "-" + text).AppendSlow("*"), flag);
    }
}
