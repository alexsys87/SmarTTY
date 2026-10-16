using System.Collections.Generic;

namespace SmarTTY;

internal sealed class ProbeResult<TClient, TScore>
{
    private readonly TClient _client;

    private readonly TScore _score;

    public TClient Client => _client;

    public TScore Score => _score;

    public ProbeResult(TClient item, TScore item2)
    {
        _client = item;
        _score = item2;
    }

    public override bool Equals(object obj)
    {
        ProbeResult<TClient, TScore> probeResult = obj as ProbeResult<TClient, TScore>;
        if (this != probeResult)
        {
            if (probeResult != null && EqualityComparer<TClient>.Default.Equals(_client, probeResult._client))
            {
                return EqualityComparer<TScore>.Default.Equals(_score, probeResult._score);
            }
            return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        return (unchecked(472931133 * -1521134295) + EqualityComparer<TClient>.Default.GetHashCode(_client)) * -1521134295 + EqualityComparer<TScore>.Default.GetHashCode(_score);
    }

    public override string ToString()
    {
        object[] array = new object[2];
        TClient value = _client;
        array[0] = ((value != null) ? value.ToString() : null);
        TScore value2 = _score;
        array[1] = ((value2 != null) ? value2.ToString() : null);
        return string.Format(null, "{{ Client = {0}, Score = {1} }}", array);
    }
}
