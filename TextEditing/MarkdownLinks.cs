using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>What a link of a Markdown document leads to.</summary>
public enum MarkdownLinkKind
{
    /// <summary>Nothing that can be opened (empty, or a scheme that would start another program).</summary>
    None,

    /// <summary>An address on the web (<see cref="MarkdownLinkTarget.Url"/>): http, https, ftp, mailto.</summary>
    Web,

    /// <summary>A heading of the same document (<see cref="MarkdownLinkTarget.Anchor"/>).</summary>
    Anchor,

    /// <summary>Another file on the same computer as the document (<see cref="MarkdownLinkTarget.Path"/>, and its anchor).</summary>
    File
}

/// <summary>Where a link of a Markdown document leads (<see cref="MarkdownLinks.Resolve"/>).</summary>
public readonly struct MarkdownLinkTarget
{
    public MarkdownLinkTarget(MarkdownLinkKind kind, string url, string path, string anchor)
    {
        Kind = kind;
        Url = url;
        Path = path;
        Anchor = anchor;
    }

    public MarkdownLinkKind Kind { get; }

    /// <summary>The address of a web link.</summary>
    public string Url { get; }

    /// <summary>The full path of a file link, with forward slashes.</summary>
    public string Path { get; }

    /// <summary>The anchor of a link to a heading (without #), also of a file link that has one; null when there is none.</summary>
    public string Anchor { get; }
}

/// <summary>The targets of the links of a Markdown document that is a file on a server.</summary>
public static class MarkdownLinks
{
    /// <summary>
    /// Where a link leads: a web address, a heading of the document (<c>#usage</c>), or a file relative to the folder of the document
    /// (<c>docs/guide.md</c>, <c>../README.md#install</c>) or to the root (<c>/etc/hosts</c>); the query of a file link is dropped and its
    /// percent escapes are decoded.
    /// </summary>
    /// <param name="url">The target as the document gives it.</param>
    /// <param name="documentPath">The path of the document on the server (forward slashes), or null when it has none.</param>
    public static MarkdownLinkTarget Resolve(string url, string documentPath)
    {
        string target = (url ?? "").Trim();
        if (target.Length == 0)
        {
            return default;
        }
        if (target[0] == '#')
        {
            return new MarkdownLinkTarget(MarkdownLinkKind.Anchor, null, null, PercentDecode(target.Substring(1)));
        }
        if (target.StartsWith("//", StringComparison.Ordinal))
        {
            return new MarkdownLinkTarget(MarkdownLinkKind.Web, "https:" + target, null, null);
        }
        string scheme = SchemeOf(target);
        if (scheme != null)
        {
            switch (scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                case "ftp":
                case "mailto":
                    return new MarkdownLinkTarget(MarkdownLinkKind.Web, target, null, null);
                default:
                    return default;
            }
        }
        if (target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return new MarkdownLinkTarget(MarkdownLinkKind.Web, "http://" + target, null, null);
        }
        string anchor = null;
        int hash = target.IndexOf('#');
        if (hash >= 0)
        {
            anchor = PercentDecode(target.Substring(hash + 1));
            target = target.Substring(0, hash);
        }
        int query = target.IndexOf('?');
        if (query >= 0)
        {
            target = target.Substring(0, query);
        }
        target = PercentDecode(target).Replace('\\', '/');
        if (target.Length == 0)
        {
            // "#x" was handled above; "?x" alone is the document itself.
            return (anchor != null) ? new MarkdownLinkTarget(MarkdownLinkKind.Anchor, null, null, anchor) : default;
        }
        string combined;
        if (target[0] == '/' || (target.Length >= 3 && IsAsciiLetter(target[0]) && target[1] == ':' && target[2] == '/'))
        {
            // From the root, or from a drive of a Windows host (C:/x).
            combined = target;
        }
        else
        {
            if (string.IsNullOrEmpty(documentPath))
            {
                return default;
            }
            string folder = documentPath.Replace('\\', '/');
            int slash = folder.LastIndexOf('/');
            folder = (slash >= 0) ? folder.Substring(0, slash + 1) : "";
            combined = folder + target;
        }
        string path = Normalize(combined);
        return (path == null) ? default : new MarkdownLinkTarget(MarkdownLinkKind.File, null, path, string.IsNullOrEmpty(anchor) ? null : anchor);
    }

    // The scheme of an address ("https" of "https://x"), or null. A single letter is a Windows drive (C:/x), not a scheme.
    private static string SchemeOf(string target)
    {
        int colon = target.IndexOf(':');
        if (colon < 2 || colon > 32 || !IsAsciiLetter(target[0]))
        {
            return null;
        }
        for (int i = 1; i < colon; i++)
        {
            char ch = target[i];
            if (!IsAsciiLetter(ch) && !(ch >= '0' && ch <= '9') && ch != '+' && ch != '.' && ch != '-')
            {
                return null;
            }
        }
        return target.Substring(0, colon);
    }

    private static bool IsAsciiLetter(char ch)
    {
        return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
    }

    // The path without "." and empty parts, ".." taking the part before it away; a path that is relative stays so. Null when ".." goes
    // above the start of a relative path.
    private static string Normalize(string path)
    {
        bool absolute = path.StartsWith("/", StringComparison.Ordinal);
        bool folder = path.EndsWith("/", StringComparison.Ordinal);
        List<string> parts = new List<string>();
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (!absolute)
                {
                    return null;
                }
                continue;
            }
            parts.Add(part);
        }
        string result = (absolute ? "/" : "") + string.Join("/", parts);
        if (folder && parts.Count > 0)
        {
            result += "/";
        }
        return result.Length == 0 ? null : result;
    }

    private static string PercentDecode(string text)
    {
        if (text.IndexOf('%') < 0)
        {
            return text;
        }
        List<byte> bytes = new List<byte>();
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            FlushBytes(bytes, result);
            result.Append(text[i]);
        }
        FlushBytes(bytes, result);
        return result.ToString();
    }

    private static void FlushBytes(List<byte> bytes, StringBuilder result)
    {
        if (bytes.Count > 0)
        {
            result.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
    }

    private static bool IsHex(char ch)
    {
        return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
    }
}
