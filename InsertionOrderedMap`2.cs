using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SmarTTY;

[DefaultMember("Item")]
public class InsertionOrderedMap<_Key, _Value> : IEnumerable<KeyValuePair<_Key, _Value>>, IEnumerable
{
    private readonly Dictionary<_Key, _Value> _dictionary;

    private readonly List<_Key> _keys;

    public _Value Get(_Key item)
    {
        return _dictionary[item];
    }

    public void Set(_Key item, _Value item2)
    {
        if (!_dictionary.ContainsKey(item))
        {
            _keys.Add(item);
        }
        _dictionary[item] = item2;
    }

    public IEnumerable<_Value> Values
    {
        get
        {
            foreach (_Key item in _keys)
            {
                if (_dictionary.TryGetValue(item, out var value))
                {
                    yield return value;
                }
            }
        }
    }

    public int Count => _dictionary.Count;

    public InsertionOrderedMap(IEqualityComparer<_Key> equalityComparer = null)
    {
        if (equalityComparer == null)
        {
            _dictionary = new Dictionary<_Key, _Value>();
        }
        else
        {
            _dictionary = new Dictionary<_Key, _Value>(equalityComparer);
        }
        _keys = new List<_Key>();
    }

    public IEnumerator<KeyValuePair<_Key, _Value>> GetEnumerator()
    {
        foreach (_Key item in _keys)
        {
            yield return new KeyValuePair<_Key, _Value>(item, _dictionary[item]);
        }
    }

    public bool TryGetValue(_Key item, out _Value item2)
    {
        return _dictionary.TryGetValue(item, out item2);
    }

    private IEnumerator GetNonGenericEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return this.GetNonGenericEnumerator();
    }
}
