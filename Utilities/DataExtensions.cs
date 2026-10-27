using System;
using System.Collections.Generic;
using System.Linq;

namespace SmarTTY.Utilities;

public static class DataExtensions
{
    public static IEnumerable<_Ty> EnsureNotNull<_Ty>(this IEnumerable<_Ty> coll)
    {
        return coll ?? new _Ty[0];
    }

    public static bool IsNullOrEmpty<_Ty>(this _Ty[] coll)
    {
        if (coll != null)
        {
            return coll.Length == 0;
        }
        return true;
    }

    public static IEnumerable<_Ty> AppendSlow<_Ty>(this IEnumerable<_Ty> coll, _Ty element)
    {
        if (coll == null)
        {
            return new _Ty[1] { element };
        }
        return coll.Concat(new _Ty[1] { element });
    }

    public static _Value GetOrDefault<_Key, _Value>(this Dictionary<_Key, _Value> dict, _Key key)
    {
        if (dict.TryGetValue(key, out var value))
        {
            return value;
        }
        return default;
    }

    public static _Value GetOrInsertNew<_Key, _Value>(this Dictionary<_Key, _Value> dict, _Key key) where _Value : new()
    {
        if (dict.TryGetValue(key, out var value))
        {
            return value;
        }
        return dict[key] = new _Value();
    }

    public static Dictionary<_Key, _Value> ToOverridingDictionary<_Key, _Value>(this IEnumerable<_Value> coll, Func<_Value, _Key> keySelector, IEqualityComparer<_Key> comparer = null)
    {
        Dictionary<_Key, _Value> dictionary = ((comparer == null) ? new Dictionary<_Key, _Value>() : new Dictionary<_Key, _Value>(comparer));
        if (coll != null)
        {
            foreach (_Value item in coll)
            {
                dictionary[keySelector(item)] = item;
            }
        }
        return dictionary;
    }

    public static bool IsNullOrEmpty(this string str)
    {
        return string.IsNullOrEmpty(str);
    }

    public static bool IsNonTrivial(this string str)
    {
        return !string.IsNullOrEmpty(str?.Trim());
    }
}
