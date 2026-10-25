using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>An encoding a remote file can be read and saved in.</summary>
public sealed class TextFileEncoding
{
    public string DisplayName { get; }

    /// <summary>The bytes that start a file in this encoding (a byte order mark), empty for most encodings.</summary>
    public byte[] Preamble { get; }

    /// <summary>The encoding without a byte order mark; it throws for a character it cannot store when <see cref="IsStrict"/>.</summary>
    internal Encoding Encoding { get; }

    /// <summary>Saving a character that the encoding cannot store is an error (instead of a "?").</summary>
    internal bool IsStrict { get; }

    internal TextFileEncoding(string displayName, Encoding encoding, byte[] preamble, bool isStrict)
    {
        DisplayName = displayName;
        Encoding = encoding;
        Preamble = preamble;
        IsStrict = isStrict;
    }

    public bool HasBom => Preamble.Length > 0;

    public override string ToString()
    {
        return DisplayName;
    }
}

public static class TextFileEncodings
{
    // Declared before the encodings: static fields are initialized in the order they are written, and the legacy code
    // pages must be available by then (.NET Framework has them built in, newer runtimes need the provider).
    private static readonly bool CodePagesRegistered = RegisterCodePages();

    private static bool RegisterCodePages()
    {
#if NET5_0_OR_GREATER
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
        return true;
    }

    private static readonly byte[] NoPreamble = new byte[0];


    public static readonly TextFileEncoding Utf8 = new TextFileEncoding("UTF-8", new UTF8Encoding(false, false), NoPreamble, isStrict: false);

    public static readonly TextFileEncoding Utf8Bom = new TextFileEncoding("UTF-8 with BOM", new UTF8Encoding(false, false), new byte[] { 0xEF, 0xBB, 0xBF }, isStrict: false);

    public static readonly TextFileEncoding Utf16Le = new TextFileEncoding("UTF-16 LE", new UnicodeEncoding(false, false, false), new byte[] { 0xFF, 0xFE }, isStrict: false);

    public static readonly TextFileEncoding Utf16Be = new TextFileEncoding("UTF-16 BE", new UnicodeEncoding(true, false, false), new byte[] { 0xFE, 0xFF }, isStrict: false);

    public static readonly TextFileEncoding Windows1251 = Legacy("Windows-1251", 1251);

    public static readonly TextFileEncoding Koi8R = Legacy("KOI8-R", 20866);

    public static readonly TextFileEncoding Cp866 = Legacy("CP866 (DOS Cyrillic)", 866);

    /// <summary>Every byte is a character, so a file in an unknown encoding survives a read and a save unchanged.</summary>
    public static readonly TextFileEncoding Latin1 = Legacy("ISO-8859-1", 28591);

    /// <summary>The encodings offered in the status bar menu.</summary>
    public static IReadOnlyList<TextFileEncoding> All { get; } = new[]
    {
        Utf8, Utf8Bom, Utf16Le, Utf16Be, Windows1251, Koi8R, Cp866, Latin1
    };

    private static TextFileEncoding Legacy(string name, int codePage)
    {
        Encoding encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        return new TextFileEncoding(name, encoding, NoPreamble, isStrict: true);
    }

    /// <summary>
    /// Guesses the encoding of a file: a byte order mark decides; otherwise valid UTF-8 is UTF-8; anything else is
    /// ISO-8859-1, because it keeps every byte and so cannot damage the file (the user may reopen it in another encoding).
    /// </summary>
    public static TextFileEncoding Detect(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return Utf8;
        }
        foreach (TextFileEncoding candidate in new[] { Utf8Bom, Utf16Le, Utf16Be })
        {
            if (StartsWith(bytes, candidate.Preamble))
            {
                return candidate;
            }
        }
        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            return Utf8;
        }
        catch (DecoderFallbackException)
        {
            return Latin1;
        }
    }

    /// <summary>Decodes the file; a byte order mark that belongs to <paramref name="encoding"/> is dropped.</summary>
    public static string Decode(byte[] bytes, TextFileEncoding encoding)
    {
        int skip = StartsWith(bytes, encoding.Preamble) ? encoding.Preamble.Length : 0;
        return encoding.Encoding.GetString(bytes, skip, bytes.Length - skip);
    }

    /// <summary>
    /// The bytes of the file: the byte order mark, if the encoding has one, and the text. Throws
    /// <see cref="InvalidOperationException"/> when the encoding cannot store a character of the text.
    /// </summary>
    public static byte[] Encode(string text, TextFileEncoding encoding)
    {
        byte[] body;
        try
        {
            body = encoding.Encoding.GetBytes(text);
        }
        catch (EncoderFallbackException exception)
        {
            string character = (exception.Index >= 0 && exception.Index < text.Length) ? " (\"" + text[exception.Index] + "\", U+" + ((int)text[exception.Index]).ToString("X4") + ")" : "";
            throw new InvalidOperationException("The text contains a character" + character + " that " + encoding.DisplayName + " cannot store. Choose another encoding, for example UTF-8.", exception);
        }
        if (encoding.Preamble.Length == 0)
        {
            return body;
        }
        byte[] result = new byte[encoding.Preamble.Length + body.Length];
        Buffer.BlockCopy(encoding.Preamble, 0, result, 0, encoding.Preamble.Length);
        Buffer.BlockCopy(body, 0, result, encoding.Preamble.Length, body.Length);
        return result;
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (prefix.Length == 0 || bytes.Length < prefix.Length)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }
        return true;
    }
}
