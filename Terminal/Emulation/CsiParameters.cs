using System;

namespace SmarTTY.Terminal.Emulation;

/// <summary>The numeric parameters of a CSI sequence.</summary>
public static class CsiParameters
{
    /// <summary>
    /// The parameters as numbers: one for each of the items or each of the defaults, whichever are more. An item that is missing or is not a number
    /// is the default of its place, or 0 when there is no default for its place (more items than defaults, as in ESC [ ; ; ; ; H).
    /// </summary>
    public static int[] Parse(string[] items, params int[] defaults)
    {
        int[] parameters = new int[Math.Max(items.Length, defaults.Length)];
        for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            if (parameterIndex >= items.Length || !int.TryParse(items[parameterIndex], out parameters[parameterIndex]))
            {
                parameters[parameterIndex] = parameterIndex < defaults.Length ? defaults[parameterIndex] : 0;
            }
        }
        return parameters;
    }
}
