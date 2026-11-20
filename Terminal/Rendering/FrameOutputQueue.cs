using System.Text;

namespace SmarTTY.Terminal.Rendering;

/// <summary>
/// The output of the remote side that has arrived and is not shown yet. The reading threads of the connection append to it; the view takes
/// it once per frame (when it is about to paint), so a burst of small chunks is parsed and painted once, not once per chunk. Appending is
/// linear in the size of the text (a <see cref="StringBuilder"/>). A piece is never cut between the two halves of a surrogate pair.
/// </summary>
public sealed class FrameOutputQueue
{
    private readonly object _lock = new object();

    private readonly StringBuilder _pending = new StringBuilder();

    // The text being taken in pieces (the backlog copied out of the builder once) and how much of it was taken.
    private string _current;

    private int _offset;

    /// <summary>Adds output (from any thread). True when the queue was empty: the caller asks for a frame then (later appends find it asked).</summary>
    public bool Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        lock (_lock)
        {
            bool wasEmpty = _pending.Length == 0 && _current == null;
            _pending.Append(text);
            return wasEmpty;
        }
    }

    /// <summary>The number of characters that wait.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pending.Length + (_current == null ? 0 : _current.Length - _offset);
            }
        }
    }

    /// <summary>
    /// Takes up to <paramref name="maxLength"/> characters from the start (all when it is 0 or less), or null when nothing waits. The piece
    /// is one character shorter when it would end with the first half of a surrogate pair (unless that is all there is). Taking a large
    /// backlog in pieces costs one copy of it, not one per piece.
    /// </summary>
    public string Take(int maxLength = 0)
    {
        lock (_lock)
        {
            if (_current == null)
            {
                if (_pending.Length == 0)
                {
                    return null;
                }
                _current = _pending.ToString();
                _offset = 0;
                _pending.Clear();
            }
            int available = _current.Length - _offset;
            int count = available;
            if (maxLength > 0 && count > maxLength)
            {
                count = maxLength;
                if (char.IsHighSurrogate(_current[_offset + count - 1]) && count > 1)
                {
                    count--;
                }
            }
            string text = (_offset == 0 && count == _current.Length) ? _current : _current.Substring(_offset, count);
            _offset += count;
            if (_offset >= _current.Length)
            {
                _current = null;
                _offset = 0;
            }
            return text;
        }
    }

    /// <summary>Drops what waits (a reset that the user asked for).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _pending.Clear();
            _current = null;
            _offset = 0;
        }
    }
}
