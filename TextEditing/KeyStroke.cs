using System;

namespace SmarTTY.TextEditing;

/// <summary>
/// A key press as the model sees it: the key with its modifiers (<see cref="VirtualKey"/>, the same value as
/// <c>KeyData</c> of a key event) and the character it types, if any. The view builds it from its own key events.
/// </summary>
public readonly struct KeyStroke : IEquatable<KeyStroke>
{
    /// <summary>The key combined with the modifiers (Shift, Control, Alt).</summary>
    public readonly VirtualKey KeyData;

    /// <summary>The typed character, or '\0' when the key press types none (a key-down event has no character).</summary>
    public readonly char Char;

    /// <summary>Builds a key press from a key with modifiers and the typed character, if any.</summary>
    public KeyStroke(VirtualKey keyData, char ch = '\0')
    {
        KeyData = keyData;
        Char = ch;
    }

    /// <summary>The key without the modifiers.</summary>
    public VirtualKey KeyCode => KeyData & VirtualKey.KeyCode;

    /// <summary>True when Shift is held.</summary>
    public bool Shift => (KeyData & VirtualKey.Shift) != 0;

    /// <summary>True when Control is held.</summary>
    public bool Control => (KeyData & VirtualKey.Control) != 0;

    /// <summary>True when Alt is held.</summary>
    public bool Alt => (KeyData & VirtualKey.Alt) != 0;

    public bool Equals(KeyStroke other)
    {
        return KeyData == other.KeyData && Char == other.Char;
    }

    public override bool Equals(object obj)
    {
        return obj is KeyStroke other && Equals(other);
    }

    public override int GetHashCode()
    {
        return unchecked((int)KeyData * 397 ^ Char);
    }

    public static bool operator ==(KeyStroke left, KeyStroke right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(KeyStroke left, KeyStroke right)
    {
        return !left.Equals(right);
    }

    public override string ToString()
    {
        return KeyData + (Char == '\0' ? string.Empty : " '" + Char + "'");
    }
}
