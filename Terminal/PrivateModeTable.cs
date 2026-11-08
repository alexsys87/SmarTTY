namespace SmarTTY.Terminal;

/// <summary>
/// The DEC private modes that a program has set with CSI ? Pn h / l, and the values it saved with CSI ? Pn s for CSI ? Pn r. The number
/// comes from the output of the remote side and can be anything that fits an int, a negative one included (the body of a CSI sequence
/// takes any character up to its final letter): a number outside the table is ignored, it is not an error.
/// </summary>
public sealed class PrivateModeTable
{
    /// <summary>
    /// The numbers 0 to <c>Size</c> - 1 are kept: the size the emulator always had. A mode from 1100 up (2004, bracketed paste, is one)
    /// is not recorded, and reads as off.
    /// </summary>
    public const int Size = 1100;

    private readonly bool[] _modes = new bool[Size];

    private readonly bool[] _saved = new bool[Size];

    public static bool IsKept(int mode)
    {
        return mode >= 0 && mode < Size;
    }

    public bool IsSet(int mode)
    {
        return IsKept(mode) && _modes[mode];
    }

    public void Set(int mode, bool enabled)
    {
        if (IsKept(mode))
        {
            _modes[mode] = enabled;
        }
    }

    /// <summary>CSI ? Pn s: remembers the present value of the mode (a mode that is not kept is left out).</summary>
    public void Save(int mode)
    {
        if (IsKept(mode))
        {
            _saved[mode] = _modes[mode];
        }
    }

    /// <summary>CSI ? Pn r: the value that was saved for the mode (false until one was); false as the result for a mode that is not kept.</summary>
    public bool TryGetSaved(int mode, out bool value)
    {
        if (!IsKept(mode))
        {
            value = false;
            return false;
        }
        value = _saved[mode];
        return true;
    }

    /// <summary>A reset of the terminal: no mode is set and nothing is saved.</summary>
    public void Clear()
    {
        System.Array.Clear(_modes, 0, _modes.Length);
        System.Array.Clear(_saved, 0, _saved.Length);
    }
}
