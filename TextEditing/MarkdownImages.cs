using System;
using System.Globalization;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>What the bytes of an image of a Markdown document are, for the decoder that draws them.</summary>
public enum MarkdownImageFormat
{
    /// <summary>Not a picture the preview can draw.</summary>
    Unknown,

    /// <summary>PNG, JPEG, GIF, BMP, ICO, TIFF or WebP: the codecs of Windows decode it.</summary>
    Raster,

    /// <summary>An SVG document.</summary>
    Svg
}

/// <summary>Where the picture of an image of a Markdown document comes from (<see cref="MarkdownImages.Locate"/>).</summary>
public enum MarkdownImageOrigin
{
    /// <summary>Nowhere the preview reads: no address, or one of a scheme that is not loaded.</summary>
    None,

    /// <summary>The address holds the bytes itself (<c>data:image/png;base64,...</c>).</summary>
    Data,

    /// <summary>A file of the same server as the document (a picture of a repository cloned there).</summary>
    ServerFile
}

/// <summary>The rules of the images of the Markdown preview that need no window: where a picture is, what its bytes are, its size in HTML.</summary>
public static class MarkdownImages
{
    /// <summary>The largest picture that is read (16 MB): a larger one is shown as its alternative text.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Where the picture of an image address is, and the key it is kept under once read: the full path of a file of the server (relative to the
    /// folder of the document; a path from <c>/</c> as it is written, see <see cref="ServerFileCandidates"/>), the address itself for a data
    /// address. A web address (http, https) is not read: the preview loads nothing from the internet.
    /// </summary>
    public static MarkdownImageOrigin Locate(string url, string documentPath, out string key)
    {
        key = null;
        string target = (url ?? "").Trim();
        if (target.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            key = target;
            return MarkdownImageOrigin.Data;
        }
        MarkdownLinkTarget link = MarkdownLinks.Resolve(target, documentPath);
        switch (link.Kind)
        {
            case MarkdownLinkKind.File:
                if (link.Path.EndsWith("/", StringComparison.Ordinal))
                {
                    return MarkdownImageOrigin.None;
                }
                key = link.Path;
                return MarkdownImageOrigin.ServerFile;
            default:
                return MarkdownImageOrigin.None;
        }
    }

    /// <summary>
    /// Whether an image address starts at <c>/</c> (but is not <c>//host</c>). GitHub takes such a path from the root of the repository, the file
    /// system from its own root, so both places are tried (<see cref="ServerFileCandidates"/>).
    /// </summary>
    public static bool IsRootRelative(string url)
    {
        string target = (url ?? "").Trim();
        return target.StartsWith("/", StringComparison.Ordinal) && !target.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>
    /// The paths of the server a picture is read from, in order: the file of <paramref name="key"/> (<see cref="Locate"/>); for an address from
    /// <c>/</c> in a git repository (<paramref name="repositoryRoot"/>, the folder that holds <c>.git</c>, or null) first the same path under the
    /// root of the repository, as GitHub shows it.
    /// </summary>
    public static string[] ServerFileCandidates(string key, bool rootRelative, string repositoryRoot)
    {
        if (!rootRelative || string.IsNullOrEmpty(repositoryRoot))
        {
            return new[] { key };
        }
        string root = repositoryRoot.TrimEnd('/');
        string underRoot = root + key;
        return underRoot == key ? new[] { key } : new[] { underRoot, key };
    }

    /// <summary>
    /// The folders a git repository that holds a document could start at, from the folder of the document up to the root of the file system
    /// (the first one with <c>.git</c> in it is the root of the repository); none for a path that is not absolute.
    /// </summary>
    public static string[] RepositoryRootCandidates(string documentPath)
    {
        string path = (documentPath ?? "").Replace('\\', '/');
        System.Collections.Generic.List<string> folders = new System.Collections.Generic.List<string>();
        int slash = path.LastIndexOf('/');
        if (slash < 0 || !(path.StartsWith("/", StringComparison.Ordinal) || (path.Length > 2 && path[1] == ':')))
        {
            return folders.ToArray();
        }
        string folder = path.Substring(0, slash);
        while (folder.Length > 0 && !(folder.Length == 2 && folder[1] == ':') && !(folder.Length == 3 && folder[0] == '/' && folder[2] == ':'))
        {
            folders.Add(folder);
            int up = folder.LastIndexOf('/');
            if (up < 0)
            {
                break;
            }
            folder = folder.Substring(0, up);
        }
        return folders.ToArray();
    }

    /// <summary>
    /// The bytes of a data address: <c>data:[type][;base64],data</c>, base64 or percent-encoded (an SVG is often given as its text). False for an
    /// address that is not one or does not decode.
    /// </summary>
    public static bool TryDecodeDataUri(string url, out byte[] bytes)
    {
        bytes = null;
        if (url == null || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int comma = url.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }
        string header = url.Substring(5, comma - 5);
        string data = url.Substring(comma + 1);
        bool base64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (base64)
            {
                StringBuilder clean = new StringBuilder(data.Length);
                foreach (char ch in Uri.UnescapeDataString(data))
                {
                    if (!char.IsWhiteSpace(ch))
                    {
                        clean.Append(ch);
                    }
                }
                bytes = Convert.FromBase64String(clean.ToString());
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
            }
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }
        return bytes.Length > 0 && bytes.Length <= MaxBytes;
    }

    /// <summary>What the bytes of a picture are, by their first bytes (the name of the file is not trusted: a badge service answers SVG for any name).</summary>
    public static MarkdownImageFormat Detect(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4)
        {
            return MarkdownImageFormat.Unknown;
        }
        if (Starts(bytes, 0x89, 0x50, 0x4E, 0x47) || Starts(bytes, 0xFF, 0xD8, 0xFF) || Starts(bytes, 0x47, 0x49, 0x46, 0x38)
            || Starts(bytes, 0x42, 0x4D) || Starts(bytes, 0x00, 0x00, 0x01, 0x00) || Starts(bytes, 0x49, 0x49, 0x2A, 0x00)
            || Starts(bytes, 0x4D, 0x4D, 0x00, 0x2A))
        {
            return MarkdownImageFormat.Raster;
        }
        if (bytes.Length >= 12 && Starts(bytes, 0x52, 0x49, 0x46, 0x46) && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return MarkdownImageFormat.Raster;
        }
        // SVG: an XML text whose first element is <svg> (after a declaration, a doctype or comments).
        string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith("<", StringComparison.Ordinal) && head.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0
            && head.IndexOf("<html", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return MarkdownImageFormat.Svg;
        }
        return MarkdownImageFormat.Unknown;
    }

    private static bool Starts(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
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

    /// <summary>
    /// A length of an HTML attribute in pixels (<c>width="200"</c>, <c>"200px"</c>); 0 for none, a percentage or anything else, and at most 10000.
    /// </summary>
    public static double ParseLength(string value)
    {
        string text = (value ?? "").Trim();
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - 2).TrimEnd();
        }
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double length) || length <= 0)
        {
            return 0;
        }
        return Math.Min(length, 10000);
    }

    /// <summary>
    /// The width an image is shown at: the width it was given, the width that keeps its proportions at the height it was given, or its own width;
    /// never wider than <paramref name="maxWidth"/> (0: no limit). The height follows from the proportions.
    /// </summary>
    public static double ShownWidth(double naturalWidth, double naturalHeight, double givenWidth, double givenHeight, double maxWidth)
    {
        double width = naturalWidth;
        if (givenWidth > 0)
        {
            width = givenWidth;
        }
        else if (givenHeight > 0 && naturalHeight > 0)
        {
            width = naturalWidth * givenHeight / naturalHeight;
        }
        if (maxWidth > 0 && width > maxWidth)
        {
            width = maxWidth;
        }
        return Math.Max(1, width);
    }
}
