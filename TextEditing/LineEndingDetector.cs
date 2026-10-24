namespace SmarTTY.TextEditing;

/// <summary>What line breaks a text uses.</summary>
public enum LineEndingKind
{
    /// <summary>There is no line break (an empty or one-line text): a new file gets LF.</summary>
    None,
    Lf,
    CrLf,
    Cr,
    /// <summary>Several kinds are used.</summary>
    Mixed
}

public static class LineEndingDetector
{
    public static LineEndingKind Detect(string text)
    {
        int lf = 0;
        int crlf = 0;
        int cr = 0;
        if (text != null)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    lf++;
                }
                else if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        crlf++;
                        i++;
                    }
                    else
                    {
                        cr++;
                    }
                }
            }
        }
        int kinds = ((lf > 0) ? 1 : 0) + ((crlf > 0) ? 1 : 0) + ((cr > 0) ? 1 : 0);
        if (kinds == 0)
        {
            return LineEndingKind.None;
        }
        if (kinds > 1)
        {
            return LineEndingKind.Mixed;
        }
        if (lf > 0)
        {
            return LineEndingKind.Lf;
        }
        return (crlf > 0) ? LineEndingKind.CrLf : LineEndingKind.Cr;
    }

    /// <summary>The short name shown in the status bar.</summary>
    public static string GetName(LineEndingKind kind)
    {
        switch (kind)
        {
            case LineEndingKind.CrLf:
                return "CRLF";
            case LineEndingKind.Cr:
                return "CR";
            case LineEndingKind.Mixed:
                return "Mixed";
            default:
                return "LF";
        }
    }
}
