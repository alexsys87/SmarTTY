using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;

namespace Sysprogs.Core;

public static class CollectionHelpers
{
    public delegate _DestType Mapper<_SourceType, _DestType>(_SourceType item);

    public delegate _ReturnType Factory<_ReturnType>();

    public delegate void Handler<_ArgType>(_ArgType item);

    public static IEnumerable<_Ty> Concat<_Ty>(params IEnumerable<_Ty>[] items)
    {
        foreach (IEnumerable<_Ty> enumerable in items)
        {
            if (enumerable == null)
            {
                continue;
            }
            foreach (_Ty item in enumerable)
            {
                yield return item;
            }
        }
    }

    public static IEnumerable<_DestType> OfTypeSafe<_DestType>(IEnumerable items)
    {
        foreach (object item in items)
        {
            if (item is _DestType)
            {
                yield return (_DestType)item;
            }
        }
    }

    public static IEnumerable<_DestType> MapSafe<_SourceType, _DestType>(IEnumerable<_SourceType> items, Mapper<_SourceType, _DestType> callback)
    {
        if (items == null)
        {
            yield break;
        }
        foreach (_SourceType item in items)
        {
            yield return callback(item);
        }
    }

    public static _DestType[] MapArray<_SourceType, _DestType>(_SourceType[] items, Mapper<_SourceType, _DestType> callback)
    {
        if (items == null)
        {
            return null;
        }
        _DestType[] array = new _DestType[items.Length];
        for (int num = 0; num < items.Length; num++)
        {
            array[num] = callback(items[num]);
        }
        return array;
    }

    public static _SourceType MaxBy<_SourceType>(IEnumerable<_SourceType> items, Mapper<_SourceType, int> callback, Mapper<_SourceType, bool> callback2 = null)
    {
        int num = int.MinValue;
        _SourceType result = default;
        foreach (_SourceType item in items)
        {
            if (callback2 == null || callback2(item))
            {
                int num2 = callback(item);
                if (num2 > num)
                {
                    num = num2;
                    result = item;
                }
            }
        }
        return result;
    }

    public static IEnumerable<_DestType> MapWhere<_SourceType, _DestType>(IEnumerable<_SourceType> items, Mapper<_SourceType, _DestType> callback, Mapper<_SourceType, bool> callback2)
    {
        if (items == null)
        {
            yield break;
        }
        foreach (_SourceType item in items)
        {
            if (callback2 == null || callback2(item))
            {
                yield return callback(item);
            }
        }
    }

    public static _DestType[] MapWhereToArray<_SourceType, _DestType>(IEnumerable<_SourceType> items, Mapper<_SourceType, _DestType> callback, Mapper<_SourceType, bool> callback2 = null)
    {
        return ToArraySafe(MapWhere(items, callback, callback2));
    }

    public static IEnumerable<_DestType> MapWhereUntyped<_DestType>(IEnumerable items, Mapper<object, _DestType> callback, Mapper<object, bool> callback2 = null)
    {
        if (items == null)
        {
            yield break;
        }
        foreach (object item in items)
        {
            if (callback2 == null || callback2(item))
            {
                yield return callback(item);
            }
        }
    }

    public static int CountWhere<_SourceType>(IEnumerable<_SourceType> items, Mapper<_SourceType, bool> callback)
    {
        if (items == null)
        {
            return 0;
        }
        int num = 0;
        foreach (_SourceType item in items)
        {
            if (callback(item))
            {
                num++;
            }
        }
        return num;
    }

    public static _SrcType FirstWhere<_SrcType>(IEnumerable<_SrcType> items, Mapper<_SrcType, bool> callback, _SrcType item2 = default(_SrcType))
    {
        if (items == null)
        {
            return default;
        }
        foreach (_SrcType item in items)
        {
            if (callback == null || callback(item))
            {
                return item;
            }
        }
        return default;
    }

    public static int IndexOfWhere<_SrcType>(_SrcType[] items, Mapper<_SrcType, bool> callback)
    {
        if (items == null || callback == null)
        {
            return -1;
        }
        for (int num = 0; num < items.Length; num++)
        {
            if (callback(items[num]))
            {
                return num;
            }
        }
        return -1;
    }

    public static int IndexOfWhereInList<_SrcType>(List<_SrcType> list, Mapper<_SrcType, bool> callback)
    {
        if (list == null || callback == null)
        {
            return -1;
        }
        for (int num = 0; num < list.Count; num++)
        {
            if (callback(list[num]))
            {
                return num;
            }
        }
        return -1;
    }

    public static bool ContainsItem<_Type>(IEnumerable<_Type> items, _Type item3)
    {
        if (item3 == null)
        {
            foreach (_Type item in items)
            {
                if (item == null)
                {
                    return true;
                }
            }
        }
        else
        {
            foreach (_Type item2 in items)
            {
                if (item3.Equals(item2))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static _Type[] ToArraySafe<_Type>(IEnumerable<_Type> items)
    {
        List<_Type> list = new List<_Type>();
        if (items != null)
        {
            foreach (_Type item in items)
            {
                list.Add(item);
            }
        }
        return list.ToArray();
    }

    public static List<_Type> ToListSafe<_Type>(IEnumerable<_Type> items)
    {
        List<_Type> list = new List<_Type>();
        if (items != null)
        {
            foreach (_Type item in items)
            {
                list.Add(item);
            }
        }
        return list;
    }

    public static bool SequenceEqualSafe<_Type>(IEnumerable<_Type> items, IEnumerable<_Type> items2)
    {
        if (items == null && items2 == null)
        {
            return true;
        }
        if (items == null || items2 == null)
        {
            return false;
        }
        using IEnumerator<_Type> enumerator = items.GetEnumerator();
        using IEnumerator<_Type> enumerator2 = items2.GetEnumerator();
        while (true)
        {
            bool flag = enumerator.MoveNext();
            bool flag2 = enumerator2.MoveNext();
            if (!flag && !flag2)
            {
                return true;
            }
            if (!flag || !flag2)
            {
                return false;
            }
            _Type current = enumerator.Current;
            _Type current2 = enumerator2.Current;
            if (current != null || current2 != null)
            {
                if (current == null || current2 == null)
                {
                    return false;
                }
                if (!current.Equals(current2))
                {
                    break;
                }
            }
        }
        return false;
    }

    public static bool IsNullOrEmpty<Ty>(Ty[] items)
    {
        if (items != null)
        {
            return items.Length == 0;
        }
        return true;
    }

    public static string JoinToString<_SourceType>(IEnumerable<_SourceType> items, Mapper<_SourceType, string> callback, string text = " ")
    {
        StringBuilder stringBuilder = new StringBuilder();
        foreach (_SourceType item in items)
        {
            if (stringBuilder.Length != 0)
            {
                stringBuilder.Append(text);
            }
            stringBuilder.Append(callback(item));
        }
        return stringBuilder.ToString();
    }

    public static Dictionary<_KeyType, _ValueType> ToDictionaryFromKeys<_KeyType, _ValueType>(IEnumerable<_KeyType> items, Mapper<_KeyType, _ValueType> callback = null)
    {
        Dictionary<_KeyType, _ValueType> dictionary = new Dictionary<_KeyType, _ValueType>();
        if (items != null)
        {
            foreach (_KeyType item in items)
            {
                if (callback == null)
                {
                    dictionary[item] = default;
                }
                else
                {
                    dictionary[item] = callback(item);
                }
            }
        }
        return dictionary;
    }

    public static Dictionary<_KeyType, _ValueType> ToDictionaryFromPairs<_KeyType, _ValueType, _InputType>(IEnumerable<_InputType> items, Mapper<_InputType, KeyValuePair<_KeyType, _ValueType>> callback = null)
    {
        Dictionary<_KeyType, _ValueType> dictionary = new Dictionary<_KeyType, _ValueType>();
        if (items != null)
        {
            foreach (_InputType item in items)
            {
                KeyValuePair<_KeyType, _ValueType> keyValuePair = callback(item);
                dictionary[keyValuePair.Key] = keyValuePair.Value;
            }
        }
        return dictionary;
    }

    public static Dictionary<string, _ValueType> ToStringDictionary<_ValueType>(IEnumerable<string> items, bool flag, Mapper<string, _ValueType> callback = null)
    {
        Dictionary<string, _ValueType> dictionary = new Dictionary<string, _ValueType>(flag ? StringComparer.InvariantCulture : StringComparer.InvariantCultureIgnoreCase);
        if (items != null)
        {
            foreach (string item in items)
            {
                if (callback == null)
                {
                    dictionary[item] = default;
                }
                else
                {
                    dictionary[item] = callback(item);
                }
            }
        }
        return dictionary;
    }

    public static IEnumerable<_KeyType> DistinctItems<_KeyType>(IEnumerable<_KeyType> items)
    {
        if (items == null)
        {
            yield break;
        }
        Dictionary<_KeyType, bool> dictionary = new Dictionary<_KeyType, bool>();
        foreach (_KeyType item in items)
        {
            if (!dictionary.ContainsKey(item))
            {
                dictionary[item] = true;
                yield return item;
            }
        }
    }

    public static IEnumerable<int> IntRange(int value2, int value3)
    {
        for (int num = value2; num <= value3; num++)
        {
            yield return num;
        }
    }

    public static _DestType[] MapIntRange<_DestType>(int value2, int value3, Mapper<int, _DestType> callback)
    {
        if (value3 < value2)
        {
            return new _DestType[0];
        }
        _DestType[] array = new _DestType[value3 - value2 + 1];
        for (int num = value2; num <= value3; num++)
        {
            array[num - value2] = callback(num);
        }
        return array;
    }

    public static bool ArrayEquals<_Ty>(_Ty[] items, _Ty[] items2, IEqualityComparer<_Ty> equalityComparer = null)
    {
        if (items == null && items2 == null)
        {
            return true;
        }
        if (items == null || items2 == null)
        {
            return false;
        }
        if (items.Length != items2.Length)
        {
            return false;
        }
        if (equalityComparer == null)
        {
            equalityComparer = EqualityComparer<_Ty>.Default;
        }
        for (int num = 0; num < items2.Length; num++)
        {
            if (!equalityComparer.Equals(items[num], items2[num]))
            {
                return false;
            }
        }
        return true;
    }

    public static bool BytesEqual(byte[] buffer, byte[] buffer2)
    {
        if (buffer == null && buffer2 == null)
        {
            return true;
        }
        if (buffer == null || buffer2 == null)
        {
            return false;
        }
        if (buffer.Length != buffer2.Length)
        {
            return false;
        }
        for (int num = 0; num < buffer2.Length; num++)
        {
            if (buffer[num] != buffer2[num])
            {
                return false;
            }
        }
        return true;
    }

    public static void SyncCollection<TSource, TDest>(ObservableCollection<TSource> observableCollection2, NotifyCollectionChangedEventArgs e, IList<TDest> list, Func<TSource, TDest> func)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                {
                    int num = e.NewStartingIndex;
                    {
                        foreach (TSource item2 in e.NewItems.OfType<TSource>())
                        {
                            list.Insert(num++, func(item2));
                        }
                        break;
                    }
                }
            case NotifyCollectionChangedAction.Remove:
                {
                    for (int num = e.OldStartingIndex + e.OldItems.Count - 1; num >= e.OldStartingIndex; num--)
                    {
                        list.RemoveAt(num);
                    }
                    break;
                }
            case NotifyCollectionChangedAction.Replace:
                {
                    for (int num = 0; num < e.NewItems.Count; num++)
                    {
                        list[e.OldStartingIndex + num] = func((TSource)e.NewItems[num]);
                    }
                    break;
                }
            case NotifyCollectionChangedAction.Move:
                {
                    if (e.OldItems.Count != 1)
                    {
                        throw new Exception("Moving multiple items is not implemented");
                    }
                    if (list is ObservableCollection<TDest> observableCollection)
                    {
                        observableCollection.Move(e.OldStartingIndex, e.NewStartingIndex);
                        break;
                    }
                    TDest item = list[e.OldStartingIndex];
                    list.RemoveAt(e.OldStartingIndex);
                    int num = e.NewStartingIndex;
                    if (num > e.OldStartingIndex)
                    {
                        num--;
                    }
                    list.Insert(num, item);
                    break;
                }
            case NotifyCollectionChangedAction.Reset:
                list.Clear();
                break;
        }
    }

    public static _Ty[] ConcatArrays<_Ty>(_Ty[] items, _Ty[] items2)
    {
        if (items == null && items2 == null)
        {
            return null;
        }
        int num = items?.Length ?? 0;
        int num2 = items2?.Length ?? 0;
        _Ty[] array = new _Ty[num + num2];
        if (num != 0)
        {
            items.CopyTo(array, 0);
        }
        if (num2 != 0)
        {
            items2.CopyTo(array, num);
        }
        return array;
    }
}
