using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// Tracks the VT100 character-set state of a terminal and translates text written while the
/// DEC Special Graphics set is active into Unicode line-drawing characters.
/// </summary>
/// <remarks>
/// Programs that run without a UTF-8 locale (ncurses applications such as mc and htop on a stock
/// OpenWrt, where TERM=xterm) draw frames and fills with ESC ( 0 ... ESC ( B instead of Unicode
/// box characters: after ESC ( 0 the bytes 0x5F-0x7E mean "lqkxj..." = "┌─┐│┘...".
/// Designations: ESC ( X selects G0, ESC ) X selects G1, SO (0x0E) / SI (0x0F) switch between them.
/// Only the sets that matter in practice are modelled: DEC Special Graphics ('0' and '2') and
/// "anything else" which is treated as plain ASCII/Latin ('B', 'A', 'U', ...).
/// </remarks>
internal sealed class DecCharsets
{
    // Replacement for the characters 0x5F..0x7E (32 entries), same table as xterm/VTE/pyte.
    private const char FirstMapped = '\u005F';

    private const string GraphicsMap =
        "\u00A0\u25C6\u2592\u2409\u240C\u240D\u240A\u00B0" +  // 5F-66  _ ` a b c d e f
        "\u00B1\u2591\u240B\u2518\u2510\u250C\u2514\u253C" +  // 67-6E  g h i j k l m n
        "\u23BA\u23BB\u2500\u23BC\u23BD\u251C\u2524\u2534" +  // 6F-76  o p q r s t u v
        "\u252C\u2502\u2264\u2265\u03C0\u2260\u00A3\u00B7";   // 77-7E  w x y z { | } ~

    private bool _g0Graphics;

    private bool _g1Graphics;

    private bool _shiftedOut;

    /// <summary>True when text written now must be translated through the graphics table.</summary>
    public bool GraphicsActive
    {
        get
        {
            return _shiftedOut ? _g1Graphics : _g0Graphics;
        }
    }

    /// <summary>Back to the power-on state: ASCII in G0 and G1, G0 active.</summary>
    public void Reset()
    {
        _g0Graphics = false;
        _g1Graphics = false;
        _shiftedOut = false;
    }

    public void ShiftOut()
    {
        _shiftedOut = true;
    }

    public void ShiftIn()
    {
        _shiftedOut = false;
    }

    /// <summary>Handles ESC slot set. Returns false when slot is not G0/G1 (G2/G3 are ignored).</summary>
    public bool Designate(char slot, char set)
    {
        bool graphics = set == '0' || set == '2';
        switch (slot)
        {
            case '(':
                _g0Graphics = graphics;
                return true;
            case ')':
                _g1Graphics = graphics;
                return true;
            default:
                return false;
        }
    }

    public string Translate(string text)
    {
        if (!GraphicsActive || string.IsNullOrEmpty(text))
        {
            return text;
        }
        StringBuilder result = null;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            int index = ch - FirstMapped;
            if (index < 0 || index >= GraphicsMap.Length)
            {
                result?.Append(ch);
                continue;
            }
            if (result == null)
            {
                result = new StringBuilder(text.Length);
                result.Append(text, 0, i);
            }
            result.Append(GraphicsMap[index]);
        }
        return result == null ? text : result.ToString();
    }
}
