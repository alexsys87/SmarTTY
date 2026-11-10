using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmarTTY;

/// <summary>
/// Pure logic of the "Install from URL..." button of the package manager window, tested by tests/ApkPackageManagerTests: which
/// links are accepted, and under which name a downloaded file is uploaded. The file is downloaded by this computer and then
/// uploaded to the host like a package file picked on the disk, so the host itself needs no access to the internet.
/// </summary>
public static class PackageUrlDownload
{
    /// <summary>More links than this in one go are refused: they are downloaded one after the other, and a pasted text that is not a list of links is not meant.</summary>
    public const int MaxUrls = 50;

    /// <summary>How many bytes from the start of a download are kept to tell what it is (<see cref="SniffExtension"/>, <see cref="LooksLikeHtml"/>).</summary>
    public const int HeadLength = 512;

    /// <summary>
    /// The links of a text, one per line or separated by blanks; empty lines and lines that start with "#" are skipped, and the
    /// same link given twice is kept once.
    /// </summary>
    public static string[] SplitUrls(string text)
    {
        List<string> urls = new List<string>();
        foreach (string rawLine in (text ?? "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string part in line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!urls.Contains(part, StringComparer.Ordinal))
                {
                    urls.Add(part);
                }
            }
        }
        return urls.ToArray();
    }

    /// <summary>
    /// The link as an absolute http or https address, or null with the reason in <paramref name="error"/>. Other schemes are
    /// refused: a file:// link is a file of this computer (the "Install package file..." button picks those), and ftp is not
    /// offered by every proxy.
    /// </summary>
    public static Uri ParseUrl(string text, out string error)
    {
        error = null;
        string value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            error = "The link is empty.";
            return null;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Not an http:// or https:// link: " + value;
            return null;
        }
        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "The link has no server name: " + value;
            return null;
        }
        return uri;
    }

    /// <summary>
    /// The file name of a Content-Disposition header ("attachment; filename=foo.deb", or "filename*=UTF-8''foo%20bar.deb", which
    /// wins when both are given), without any folders; null when there is none.
    /// </summary>
    public static string FileNameFromContentDisposition(string header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }
        string plain = null;
        string extended = null;
        foreach (string rawPart in SplitParameters(header))
        {
            string part = rawPart.Trim();
            int equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            string name = part.Substring(0, equals).Trim().ToLowerInvariant();
            string value = part.Substring(equals + 1).Trim();
            if (name == "filename*")
            {
                // RFC 5987: charset'language'percent-encoded value.
                int first = value.IndexOf('\'');
                int second = first < 0 ? -1 : value.IndexOf('\'', first + 1);
                if (second > 0)
                {
                    extended = DecodePercent(value.Substring(second + 1), value.Substring(0, first));
                }
            }
            else if (name == "filename")
            {
                plain = Unquote(value);
            }
        }
        return StripFolders(extended ?? plain);
    }

    /// <summary>The last segment of the path of the link, decoded ("foo%2B1.deb" is "foo+1.deb"); null when the path ends with a slash.</summary>
    public static string FileNameFromUri(Uri uri)
    {
        if (uri == null)
        {
            return null;
        }
        string path = uri.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string segment = slash < 0 ? path : path.Substring(slash + 1);
        return StripFolders(Uri.UnescapeDataString(segment));
    }

    /// <summary>
    /// A name that <see cref="LocalPackageFiles.IsSafeFileName"/> accepts and that Windows can store, made from <paramref name="name"/>:
    /// the characters a shell would take for something else become "_", and so do ":" (an alternate data stream on NTFS) and a "-" at
    /// the start (the name of a downloaded file is chosen by the server, so it is not left to the user to rename it). Null when
    /// nothing usable is left.
    /// </summary>
    public static string MakeSafeFileName(string name)
    {
        name = StripFolders(name);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        StringBuilder builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            bool safe = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || "._+~@%,=-".IndexOf(c) >= 0;
            builder.Append(safe ? c : '_');
        }
        if (builder[0] == '-')
        {
            builder[0] = '_';
        }
        // Windows drops the dots at the end of a name.
        string result = builder.ToString().TrimEnd('.');
        // "." and ".." are not names of files; a name of underscores only tells nothing either.
        if (result.Trim('.', '_').Length == 0)
        {
            return null;
        }
        // CON, NUL, COM1, ... are devices on Windows, also with an extension ("nul.deb").
        string stem = result.Split('.')[0].ToUpperInvariant();
        if (ReservedWindowsNames.Contains(stem))
        {
            result = "_" + result;
        }
        return LocalPackageFiles.IsSafeFileName(result) ? result : null;
    }

    /// <summary>
    /// The extension of a package whose kind is told by its first bytes, for a link that does not name the file
    /// ("https://host/download?id=5"): ".deb" (an ar archive whose first member is debian-binary) or ".rpm" (the lead of an
    /// RPM package). Null for anything else: .ipk, .apk and the packages of pacman and pkg are plain compressed archives,
    /// which do not tell what they hold.
    /// </summary>
    public static string SniffExtension(byte[] head, int count)
    {
        if (head == null)
        {
            return null;
        }
        count = Math.Min(count, head.Length);
        if (StartsWith(head, count, Encoding.ASCII.GetBytes("!<arch>\ndebian-binary")))
        {
            return ".deb";
        }
        if (StartsWith(head, count, new byte[] { 0xED, 0xAB, 0xEE, 0xDB }))
        {
            return ".rpm";
        }
        return null;
    }

    /// <summary>
    /// True when the download is a web page rather than a file: a link to the page of a release instead of its file, or a login
    /// page of a server that wants a password. No package starts like that.
    /// </summary>
    public static bool LooksLikeHtml(byte[] head, int count)
    {
        if (head == null)
        {
            return false;
        }
        count = Math.Min(count, head.Length);
        // A UTF-8 byte order mark may come first.
        int start = StartsWith(head, count, new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        string text = Encoding.ASCII.GetString(head, start, count - start).TrimStart(' ', '\t', '\r', '\n').ToLowerInvariant();
        return text.StartsWith("<!doctype html", StringComparison.Ordinal) || text.StartsWith("<html", StringComparison.Ordinal) || text.StartsWith("<head", StringComparison.Ordinal);
    }

    /// <summary>
    /// The name under which the downloaded file is uploaded and installed, or null with the reason in <paramref name="error"/>.
    /// The name comes from the Content-Disposition header, then from the address the download ended at (after redirections:
    /// SourceForge's ".../foo.deb/download"), then from the link itself; the first one that is a package of the host's package
    /// manager is taken. When none is, the first bytes may still tell a .deb or an .rpm, and the extension is added.
    /// </summary>
    public static string ChooseFileName(ILocalPackageFileInstaller installer, string contentDisposition, Uri requested, Uri final, byte[] head, int count, out string error)
    {
        error = null;
        if (installer == null)
        {
            error = "The package manager of the server does not install package files.";
            return null;
        }
        if (LooksLikeHtml(head, count))
        {
            error = "The link leads to a web page, not to a package file: " + requested + "\n\nCopy the link of the file itself (for example, the link of the download button), not the link of the page that offers it.";
            return null;
        }
        List<string> candidates = new[] { FileNameFromContentDisposition(contentDisposition), FileNameFromUri(final), FileNameFromUri(requested) }
            .Select(MakeSafeFileName).Where(name => name != null).Distinct(StringComparer.Ordinal).ToList();
        string package = candidates.FirstOrDefault(installer.IsPackageFile);
        if (package != null)
        {
            return package;
        }
        string extension = SniffExtension(head, count);
        if (extension != null && installer.IsPackageFile("package" + extension))
        {
            string stem = candidates.FirstOrDefault() ?? "package";
            return stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? stem : stem + extension;
        }
        error = "The file " + (candidates.FirstOrDefault() ?? "of the link") + " is not a package that " + installer.InstallToolName + " installs (" + DescribeFilter(installer.LocalPackageFileFilter) + "):\n\n" + requested;
        return null;
    }

    /// <summary>"Debian packages (*.deb)" from a file dialog filter "Debian packages (*.deb)|*.deb".</summary>
    public static string DescribeFilter(string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return "package files";
        }
        int bar = filter.IndexOf('|');
        return bar < 0 ? filter : filter.Substring(0, bar);
    }

    /// <summary>"512 bytes", "1.5 KB", "12.3 MB", "1.20 GB".</summary>
    public static string FormatSize(long bytes)
    {
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (bytes < 1024)
        {
            return bytes + " bytes";
        }
        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.0", invariant) + " KB";
        }
        if (bytes < 1024L * 1024 * 1024)
        {
            return (bytes / (1024.0 * 1024)).ToString("0.0", invariant) + " MB";
        }
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", invariant) + " GB";
    }

    /// <summary>
    /// The line of the download window: "Downloading 2 of 3: foo.deb, 45% (12.3 MB of 27.0 MB)", or with the size received so far when the
    /// server did not tell the total.
    /// </summary>
    public static string FormatProgress(int index, int count, string name, long received, long total)
    {
        string prefix = "Downloading " + (count > 1 ? (index + 1) + " of " + count + ": " : "") + name;
        if (total > 0)
        {
            long percent = Math.Min(100, received * 100 / total);
            return prefix + ", " + percent + "% (" + FormatSize(received) + " of " + FormatSize(total) + ")";
        }
        return prefix + ", " + FormatSize(received);
    }

    /// <summary>The value of the progress bar (0..100) of one download among <paramref name="count"/>; -1 when the total is not known.</summary>
    public static double BarValue(int index, int count, long received, long total)
    {
        if (total <= 0 || count <= 0)
        {
            return -1;
        }
        double part = Math.Min(1.0, (double)received / total);
        return (index + part) * 100.0 / count;
    }

    private static readonly HashSet<string> ReservedWindowsNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static bool StartsWith(byte[] data, int count, byte[] prefix)
    {
        if (count < prefix.Length)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    // Splits at the semicolons that are not inside double quotes.
    private static IEnumerable<string> SplitParameters(string header)
    {
        StringBuilder current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < header.Length; i++)
        {
            char c = header[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == '\\' && quoted && i + 1 < header.Length)
            {
                current.Append(c).Append(header[++i]);
                continue;
            }
            if (c == ';' && !quoted)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        yield return current.ToString();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 1; i < value.Length - 1; i++)
            {
                // Only \" and \\ are escapes: servers put Windows paths (C:\x\foo.deb) in the name as they are.
                if (value[i] == '\\' && i + 1 < value.Length - 1 && (value[i + 1] == '"' || value[i + 1] == '\\'))
                {
                    i++;
                }
                builder.Append(value[i]);
            }
            return builder.ToString();
        }
        return value;
    }

    private static string DecodePercent(string value, string charset)
    {
        List<byte> bytes = new List<byte>();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && IsHex(value[i + 1]) && IsHex(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }
        Encoding encoding = string.Equals(charset, "iso-8859-1", StringComparison.OrdinalIgnoreCase) ? Encoding.GetEncoding("iso-8859-1") : Encoding.UTF8;
        return encoding.GetString(bytes.ToArray());
    }

    private static bool IsHex(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    // Only the name: a server must not choose a folder ("../../x.deb", "C:\x.deb").
    private static string StripFolders(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        int separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        string result = (separator < 0 ? name : name.Substring(separator + 1)).Trim();
        return result.Length == 0 ? null : result;
    }
}
