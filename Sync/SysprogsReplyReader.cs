using System;
using System.Collections.Generic;

namespace SmarTTY.Sync;

/// <summary>
/// Splits what the SysprogsSync helper sends back on its standard output into replies. The records have a fixed size
/// (<see cref="ActionDoneSize"/> and <see cref="ActionFailedSize"/> bytes) and the output comes in pieces that do not know about
/// them (SSH packets, reads of any size), so a record is often cut in two: the bytes of the first half wait here for the second.
/// The decoding is kept apart from the SSH channel so that it can be checked without one.
/// </summary>
internal sealed class SysprogsReplyReader
{
    /// <summary>The first byte of a record; the values are those of the helper.</summary>
    internal enum Reply : byte
    {
        Unknown = 0,
        ActionFailed = 1,
        ActionDone = 2
    }

    /// <summary>The reply byte and its padding.</summary>
    public const int ActionDoneSize = 4;

    /// <summary>The reply byte and its padding, the index of the action (int) and the error code (int).</summary>
    public const int ActionFailedSize = 12;

    private readonly List<byte> _pending = new List<byte>();

    /// <summary>An action failed: its index in the list that was sent, and the error code of the helper.</summary>
    public event Action<int, int> ActionFailed;

    /// <summary>The output is not a sequence of replies. Raised once, nothing is read after it.</summary>
    public event Action Corrupt;

    public bool IsCorrupted { get; private set; }

    /// <summary>The bytes of a record whose end has not come yet (less than the longest record).</summary>
    public int PendingByteCount => _pending.Count;

    /// <summary>Reads the replies in <paramref name="count"/> bytes of <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    public void Feed(byte[] buffer, int offset, int count)
    {
        if (IsCorrupted || count <= 0)
        {
            return;
        }
        byte[] data = buffer;
        int index = offset;
        int end = offset + count;
        if (_pending.Count != 0)
        {
            // The rest of a record that came before, and the new bytes behind it: one array for the whole call.
            for (int i = 0; i < count; i++)
            {
                _pending.Add(buffer[offset + i]);
            }
            data = _pending.ToArray();
            index = 0;
            end = data.Length;
            _pending.Clear();
        }
        bool incomplete = false;
        while (index < end && !IsCorrupted && !incomplete)
        {
            switch ((Reply)data[index])
            {
                case Reply.ActionDone:
                    if (end - index < ActionDoneSize)
                    {
                        incomplete = true;
                        break;
                    }
                    index += ActionDoneSize;
                    break;
                case Reply.ActionFailed:
                    if (end - index < ActionFailedSize)
                    {
                        incomplete = true;
                        break;
                    }
                    int actionIndex = BitConverter.ToInt32(data, index + 4);
                    int errorCode = BitConverter.ToInt32(data, index + 8);
                    index += ActionFailedSize;
                    ActionFailed?.Invoke(actionIndex, errorCode);
                    break;
                default:
                    IsCorrupted = true;
                    index++;
                    Corrupt?.Invoke();
                    break;
            }
        }
        if (incomplete)
        {
            // The record goes on in the next piece: keep what there is of it (never the caller's buffer itself).
            for (int i = index; i < end; i++)
            {
                _pending.Add(data[i]);
            }
        }
    }
}
