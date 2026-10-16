using SmarTTY;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Sysprogs.Core.Tools;

public static class DataExtensions
{
    private class NullEqualsEmptyComparer : IEqualityComparer<string>
    {
        public static NullEqualsEmptyComparer Instance = new NullEqualsEmptyComparer();

        public bool Equals(string x, string y)
        {
            return StringComparer.InvariantCulture.Equals(x ?? "", y ?? "");
        }

        public int GetHashCode(string obj)
        {
            return StringComparer.InvariantCulture.GetHashCode(obj ?? "");
        }
    }

    public static IEnumerable<_Ty> EnsureNotNull<_Ty>(this IEnumerable<_Ty> coll)
    {
        return coll ?? new _Ty[0];
    }

    public static IEnumerable EnsureNotNull(this IEnumerable coll)
    {
        return coll ?? new object[0];
    }

    public static bool IsNullOrEmpty<_Ty>(this _Ty[] coll)
    {
        if (coll != null)
        {
            return coll.Length == 0;
        }
        return true;
    }

    public static bool IsNullOrEmpty<_Ty>(this List<_Ty> coll)
    {
        if (coll != null)
        {
            return coll.Count == 0;
        }
        return true;
    }

    public static _Ty TryGet<_Ty>(this _Ty[] arr, int index)
    {
        if (arr == null || index < 0 || index >= arr.Length)
        {
            return default;
        }
        return arr[index];
    }

    public static void TrySet<_Ty>(this _Ty[] arr, int index, _Ty value)
    {
        if (arr != null && index >= 0 && index < arr.Length)
        {
            arr[index] = value;
        }
    }

    public static _Ty? TryGet2<_Ty>(this _Ty[] arr, int index) where _Ty : struct
    {
        if (arr == null || index < 0 || index >= arr.Length)
        {
            return null;
        }
        return arr[index];
    }

    public static _Ty TryGet<_Ty>(this List<_Ty> arr, int index)
    {
        if (arr == null || index < 0 || index >= arr.Count)
        {
            return default;
        }
        return arr[index];
    }

    public static bool AddIfNotNull<_Ty>(this List<_Ty> list, _Ty value) where _Ty : class
    {
        if (value == null)
        {
            return false;
        }
        list.Add(value);
        return true;
    }

    public static bool HasData<_Ty>(this _Ty[] array)
    {
        if (array == null)
        {
            return false;
        }
        return array.Length != 0;
    }

    public static IEnumerable<_Ty> AppendSlow<_Ty>(this IEnumerable<_Ty> coll, _Ty element)
    {
        if (coll == null)
        {
            return new _Ty[1] { element };
        }
        return coll.Concat(new _Ty[1] { element });
    }

    public static _Ty[] AppendSlow<_Ty>(this _Ty[] coll, _Ty element)
    {
        if (coll == null)
        {
            return new _Ty[1] { element };
        }
        return coll.Concat(new _Ty[1] { element }).ToArray();
    }

    public static IEnumerable<_Ty> PrependSlow<_Ty>(this IEnumerable<_Ty> coll, _Ty element)
    {
        return new _Ty[1] { element }.Concat(coll);
    }

    public static IEnumerable<_Ty> WhereNotNull<_Ty>(this IEnumerable<_Ty> coll) where _Ty : class
    {
        return coll.Where((_Ty value) => value != null);
    }

    public static _Ty[] WhereNotNullArray<_Ty>(this IEnumerable<_Ty> coll) where _Ty : class
    {
        return coll.Where((_Ty value) => value != null).ToArray();
    }

    public static _Target[] ArraySelect<_Source, _Target>(this IEnumerable<_Source> coll, Func<_Source, _Target> selector)
    {
        return coll?.Select(selector)?.ToArray() ?? new _Target[0];
    }

    public static _Source[] ArrayWhere<_Source>(this IEnumerable<_Source> coll, Func<_Source, bool> predicate)
    {
        return coll?.Where(predicate)?.ToArray() ?? new _Source[0];
    }

    public static IEnumerable<string> ExceptNullOrEmpty(this IEnumerable<string> coll)
    {
        return coll.Where((string value) => !string.IsNullOrEmpty(value));
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

    public static _Value GetOrInsertNew<_Key, _Value>(this InsertionOrderedMap<_Key, _Value> dict, _Key key, object p) where _Value : new()
    {
        if (dict.TryGetValue(key, out var result))
        {
            return result;
        }
        _Value result2;
        dict.Set(key, result2 = new _Value());
        return result2;
    }

    public static _Value GetOrInsert<_Key, _Value>(this Dictionary<_Key, _Value> dict, _Key key, Func<_Key, _Value> creator)
    {
        if (dict.TryGetValue(key, out var value))
        {
            return value;
        }
        return dict[key] = creator(key);
    }

    public static _Value GetOrInsert<_Key, _Value>(this InsertionOrderedMap<_Key, _Value> dict, _Key key, Func<_Key, _Value> creator)
    {
        if (dict.TryGetValue(key, out var result))
        {
            return result;
        }
        _Value result2;
        dict.Set(key, result2 = creator(key));
        return result2;
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

    public static Dictionary<_Key, _Value> ToOverridingDictionary<_Key, _Value, _Source>(this IEnumerable<_Source> coll, Func<_Source, _Key> keySelector, Func<_Source, _Value> valueSelector, IEqualityComparer<_Key> comparer = null)
    {
        Dictionary<_Key, _Value> dictionary = ((comparer == null) ? new Dictionary<_Key, _Value>() : new Dictionary<_Key, _Value>(comparer));
        if (coll != null)
        {
            foreach (_Source item in coll)
            {
                dictionary[keySelector(item)] = valueSelector(item);
            }
        }
        return dictionary;
    }

    public static HashSet<_Ty> ToHashSet<_Ty>(this IEnumerable<_Ty> coll, IEqualityComparer<_Ty> comparer = null)
    {
        HashSet<_Ty> hashSet = ((comparer == null) ? new HashSet<_Ty>() : new HashSet<_Ty>(comparer));
        if (coll != null)
        {
            foreach (_Ty item in coll)
            {
                hashSet.Add(item);
            }
        }
        return hashSet;
    }

    public static HashSet<_Ty> ToHashSet2<_Ty>(this IEnumerable<_Ty> coll, IEqualityComparer<_Ty> comparer = null)
    {
        return coll.ToHashSet(comparer);
    }

    public static bool IsNullOrEmpty(this string str)
    {
        return string.IsNullOrEmpty(str);
    }

    public static bool IsNonTrivial(this string str)
    {
        return !string.IsNullOrEmpty(str?.Trim());
    }

    public static string ReplaceIfNullOrEmpty(this string str, string anotherStr)
    {
        if (string.IsNullOrEmpty(str))
        {
            return anotherStr;
        }
        return str;
    }

    public static string WithForwardSlashes(this string str)
    {
        return str?.Replace('\\', '/');
    }

    public static string NullIfEmpty(this string str)
    {
        if (!string.IsNullOrEmpty(str))
        {
            return str;
        }
        return null;
    }

    public static Ty[] NullIfEmpty<Ty>(this Ty[] arr)
    {
        if (arr.Length != 0)
        {
            return arr;
        }
        return null;
    }

    public static string[] SplitRemovingEmpty(this string str, params char[] separator)
    {
        return str.Split(separator, StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool ContainsInsensitive(this string str, string str2)
    {
        if (string.IsNullOrEmpty(str2))
        {
            return true;
        }
        if (str == null)
        {
            return false;
        }
        return str.IndexOf(str2, StringComparison.InvariantCultureIgnoreCase) != -1;
    }

    public static bool EqualsInsensitive(this string str, string str2)
    {
        return StringComparer.InvariantCultureIgnoreCase.Compare(str, str2) == 0;
    }

    public static string UpcaseFirstLetter(this string str)
    {
        if (string.IsNullOrEmpty(str))
        {
            return str;
        }
        return char.ToUpper(str[0]) + str.Substring(1);
    }

    public static string OverrideIfDefault(this string str, string value)
    {
        if (string.IsNullOrEmpty(str))
        {
            return value;
        }
        return str;
    }

    public static int? IndexOf<_Ty>(this IEnumerable<_Ty> coll, Func<_Ty, bool> predicate)
    {
        int num = 0;
        foreach (_Ty item in coll)
        {
            if (predicate(item))
            {
                return num;
            }
            num++;
        }
        return null;
    }

    public static int? MaxOrDefault<_Ty>(this IEnumerable<_Ty> coll, Func<_Ty, int> selector)
    {
        if (coll.ToArray().Length == 0)
        {
            return null;
        }
        return coll.Max(selector);
    }

    public static Dictionary<_Key, _Value> Subtract<_Key, _Value>(this Dictionary<_Key, _Value> dict, IEnumerable<KeyValuePair<_Key, _Value>> values)
    {
        if (values == null)
        {
            return dict;
        }
        Dictionary<_Key, _Value> dictionary = new Dictionary<_Key, _Value>(dict);
        IEqualityComparer<_Value> equalityComparer = EqualityComparer<_Value>.Default;
        if (typeof(_Value) == typeof(string))
        {
            equalityComparer = (IEqualityComparer<_Value>)NullEqualsEmptyComparer.Instance;
        }
        foreach (KeyValuePair<_Key, _Value> value2 in values)
        {
            if (dictionary.TryGetValue(value2.Key, out var value) && equalityComparer.Equals(value, value2.Value))
            {
                dictionary.Remove(value2.Key);
            }
        }
        return dictionary;
    }

    public static int IndexOf(this byte[] array, byte value, int startIndex = 0, int maxLength = int.MaxValue)
    {
        int num = startIndex + maxLength;
        if (num < startIndex)
        {
            num = array.Length;
        }
        for (int num2 = startIndex; num2 < num; num2++)
        {
            if (array[num2] == value)
            {
                return num2;
            }
        }
        return -1;
    }
}
