using System;
using System.Runtime.InteropServices;
using SmarTTY.TextEditing;

namespace SmarTTY.Native;

/// <summary>
/// The colours of the system that the settings take as defaults (stage 5C; they were the <c>SystemColors</c> of <c>System.Drawing</c>,
/// which read the same <c>GetSysColor</c>).
/// </summary>
internal static class SystemColorValues
{
    private const int ColorButtonFace = 15;

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    /// <summary>The face of a button (<c>SystemColors.ButtonFace</c>); the light grey of Windows where the system cannot be asked.</summary>
    public static RgbColor ButtonFace => Read(ColorButtonFace, RgbColor.FromArgb(240, 240, 240));

    private static RgbColor Read(int index, RgbColor fallback)
    {
        try
        {
            // COLORREF is 0x00BBGGRR.
            uint value = GetSysColor(index);
            return RgbColor.FromArgb(255, (int)(value & 0xFF), (int)((value >> 8) & 0xFF), (int)((value >> 16) & 0xFF));
        }
        catch (Exception)
        {
            // Not Windows (the Mono stands): no user32.
            return fallback;
        }
    }
}
