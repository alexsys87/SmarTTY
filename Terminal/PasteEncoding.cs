using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>What <see cref="PasteEncoding.Analyze"/> found out about a text that is going to be sent in an encoding.</summary>
public sealed class PasteEncodingResult
{
    /// <summary>The text to send: composed (NFC) for legacy encodings, curly quotes and the like replaced where the encoding lacks them.</summary>
    public string Text { get; set; }

    /// <summary>The characters (a surrogate pair counts as one) that the encoding cannot represent, each once, in the order of appearance.</summary>
    public List<string> Unmappable { get; } = new List<string>();

    /// <summary>How many characters of <see cref="Text"/> the encoding cannot represent.</summary>
    public int UnmappableCount { get; set; }

    public bool IsLossless => UnmappableCount == 0;
}

/// <summary>
/// Checks pasted text against the encoding of a COM/TCP/Telnet session. A single-byte code page (Windows-1251, CP866,
/// KOI8-R, ISO-8859-x) silently turns every character it lacks into "?": an emoji, a curly quote copied from a web page
/// or a decomposed letter (the form macOS file names have) used to reach the device damaged without a word.
/// Free of WinForms so that the test harness can compile it.
/// </summary>
public static class PasteEncoding
{
    private const int Utf8CodePage = 65001;

    /// <summary>Encodings that hold every character; nothing needs to be checked or composed for them.</summary>
    public static bool IsUnicode(Encoding encoding)
    {
        if (encoding == null)
        {
            return true;
        }
        switch (encoding.CodePage)
        {
            case Utf8CodePage:
            case 1200:
            case 1201:
            case 12000:
            case 12001:
            case 54936:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Prepares <paramref name="text"/> for <paramref name="encoding"/>: composes decomposed letters, replaces
    /// typographic characters the encoding lacks with their ASCII form, and lists what still cannot be represented.
    /// </summary>
    public static PasteEncodingResult Analyze(string text, Encoding encoding)
    {
        PasteEncodingResult result = new PasteEncodingResult { Text = text ?? "" };
        if (string.IsNullOrEmpty(text) || IsUnicode(encoding))
        {
            return result;
        }
        string composed = Compose(text);
        List<string> missing = FindUnmappable(composed, encoding, out int count);
        if (missing.Count == 0)
        {
            result.Text = composed;
            return result;
        }
        // A curly quote or a long dash becomes its ASCII form when the encoding has that.
        Dictionary<string, string> folded = new Dictionary<string, string>();
        foreach (string element in missing)
        {
            string replacement = PasteSanitizer.ReplaceTypographicCharacters(element);
            if (replacement != element && FindUnmappable(replacement, encoding, out _).Count == 0)
            {
                folded[element] = replacement;
            }
        }
        string text2 = folded.Count == 0 ? composed : Replace(composed, folded);
        List<string> remaining = FindUnmappable(text2, encoding, out int remainingCount);
        result.Text = text2;
        result.Unmappable.AddRange(remaining);
        result.UnmappableCount = remainingCount;
        return result;
    }

    /// <summary>The characters in "U+1F600" form with the character itself, for a message (at most <paramref name="max"/> of them).</summary>
    public static string Describe(IList<string> elements, int max)
    {
        List<string> parts = new List<string>();
        for (int i = 0; i < elements.Count && i < max; i++)
        {
            string element = elements[i];
            int codePoint = char.ConvertToUtf32(element, 0);
            string shown = codePoint < 0x20 || (codePoint >= 0x7f && codePoint < 0xa0) ? "" : element + " ";
            parts.Add(shown + "(U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture) + ")");
        }
        string list = string.Join(", ", parts);
        return elements.Count > max ? list + ", ..." : list;
    }

    private static string Compose(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // A lone surrogate: the text stays as it is and is reported below.
            return text;
        }
    }

    private static string Replace(string text, Dictionary<string, string> map)
    {
        StringBuilder builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            string element = ElementAt(text, i);
            if (map.TryGetValue(element, out string replacement))
            {
                builder.Append(replacement);
            }
            else
            {
                builder.Append(element);
            }
            i += element.Length - 1;
        }
        return builder.ToString();
    }

    private static string ElementAt(string text, int index)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return text.Substring(index, 2);
        }
        return text.Substring(index, 1);
    }

    private static List<string> FindUnmappable(string text, Encoding encoding, out int count)
    {
        RecordingFallback recorder = new RecordingFallback();
        Encoding probe = (Encoding)encoding.Clone();
        probe.EncoderFallback = recorder;
        probe.GetByteCount(text);
        count = recorder.Count;
        return recorder.Distinct;
    }

    /// <summary>Replaces what cannot be encoded with "?" like the default fallback, and records it.</summary>
    private sealed class RecordingFallback : EncoderFallback
    {
        public int Count { get; set; }

        public List<string> Distinct { get; } = new List<string>();

        public override int MaxCharCount => 1;

        public override EncoderFallbackBuffer CreateFallbackBuffer()
        {
            return new RecordingBuffer(this);
        }

        public void Record(string element)
        {
            Count++;
            if (!Distinct.Contains(element))
            {
                Distinct.Add(element);
            }
        }
    }

    private sealed class RecordingBuffer : EncoderFallbackBuffer
    {
        private readonly RecordingFallback _owner;

        private int _remaining;

        public RecordingBuffer(RecordingFallback owner)
        {
            _owner = owner;
        }

        public override int Remaining => _remaining;

        public override bool Fallback(char charUnknown, int index)
        {
            _owner.Record(charUnknown.ToString());
            _remaining = 1;
            return true;
        }

        public override bool Fallback(char charUnknownHigh, char charUnknownLow, int index)
        {
            _owner.Record(new string(new[] { charUnknownHigh, charUnknownLow }));
            _remaining = 1;
            return true;
        }

        public override char GetNextChar()
        {
            if (_remaining == 0)
            {
                return '\0';
            }
            _remaining--;
            return '?';
        }

        public override bool MovePrevious()
        {
            if (_remaining == 0)
            {
                _remaining = 1;
                return true;
            }
            return false;
        }
    }
}
