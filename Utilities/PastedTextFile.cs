using System;

namespace SmarTTY.Utilities;

/// <summary>The file that a paste too large to be typed is sent as (see SessionWrapper.UploadPastedText).</summary>
public static class PastedTextFile
{
    /// <summary>
    /// A name that no other paste has: the time to the second and twelve characters at random. With the time alone, two pastes in
    /// the same second wrote the same local file (the first upload could then send the text of the second) and the second upload
    /// replaced the first on the server. Letters, digits, dot, minus and underscore only, so it is the same name on every system.
    /// </summary>
    public static string NewName(DateTime now)
    {
        return "paste-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".txt";
    }
}
