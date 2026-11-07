using SmarTTY.Native;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Serialization;

namespace SmarTTY.Sync;

public class TarExtractor
{
    private class PendingSymlink
    {
        public string Path;

        public string Target;

        public string RelativePath;

        public bool IsHardLink;

        public string NormalizedTarget => Target.TrimStart('/', '\\').Replace('/', '\\');

        public override string ToString()
        {
            return $"{Path} => {Target}";
        }
    }

    public delegate void TarProgressHandler(bool isSymlink, string entryName, long entryOffset, long bytesDone, int filesDone, long totalBytes);

    public delegate void PathRewriteHandler(string rootPath, ref string relativePath);

    public delegate void OverwriteHandler(string filePath, ref bool overwrite);

    public class PathTooLongEventArgs : EventArgs
    {
        public string Path;

        public bool IsDirectory;

        public bool Handled;
    }

    private class ResolvedLink
    {
        public readonly string LinkPath;

        public readonly string Target;

        public readonly string FullTargetPath;

        public bool Created;

        public readonly string OriginalName;

        public ResolvedLink(string linkPath, string target, string originalName, string baseDir)
        {
            LinkPath = linkPath;
            this.Target = target;
            this.FullTargetPath = Path.Combine(baseDir ?? Path.GetDirectoryName(linkPath), target);
            this.OriginalName = originalName;
        }
    }

    private Stream _stream;

    private string _rootPath;

    private readonly IExtractionTarget _target;

    private byte[] _buffer = new byte[1048576];

    private static DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

    // The times that a file can have on Windows (a FILETIME starts in 1601) and in a DateTime; a time outside is clamped.
    private static readonly long MinUnixSeconds = (long)(new DateTime(1601, 1, 2) - UnixEpoch).TotalSeconds;

    private static readonly long MaxUnixSeconds = (long)(DateTime.MaxValue - UnixEpoch).TotalSeconds;

    /// <summary>The data of a GNU 'L', 'K' or a pax 'x', 'g' record is a few names; a bigger one is a broken or malicious archive.</summary>
    private const int MaxExtensionHeaderSize = 16 * 1024 * 1024;

    private DirectoryStatistics _statistics = new DirectoryStatistics();

    private List<PendingSymlink> _symlinks = new List<PendingSymlink>();

    public TarProgressHandler Progress;

    public bool PreserveTimestamps;

    private bool _longPathMode;

    private HashSet<string> _exactNames = new HashSet<string>();

    private HashSet<string> _caseInsensitiveNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

    public event Action<object, string> Log;

    public event PathRewriteHandler PathRewriting;

    public event OverwriteHandler FileOverwriting;

    public event EventHandler<PathTooLongEventArgs> PathTooLong;

    public TarExtractor(Stream stream, string rootPath, IExtractionTarget extractionTarget = null)
    {
        _stream = stream;
        _rootPath = rootPath;
        _target = extractionTarget;
    }

    private byte[] ReadHeader(out string entryName, out ulong size, out byte typeFlag)
    {
        byte[] buffer = new byte[512];
        int bytesRead = _stream.Read(buffer, 0, 512);
        if (bytesRead == 0 || buffer[0] == 0)
        {
            entryName = null;
            size = 0uL;
            typeFlag = 0;
            return null;
        }
        Encoding utf8 = Encoding.UTF8;
        if (bytesRead != 512)
        {
            throw new IOException("Incomplete TAR header");
        }
        entryName = utf8.GetString(buffer, 0, 100).TrimEnd(default(char));
        if (entryName == "")
        {
            throw new Exception("Invalid file name in a TAR record");
        }
        // Octal, or base-256 for 8 GiB and more (GNU tar, BusyBox tar): the same reader as the archive checker has.
        long parsedSize = TarArchiveChecker.ParseNumber(buffer, 124, 12);
        if (parsedSize < 0)
        {
            throw new InvalidDataException("Invalid size in a TAR record");
        }
        size = (ulong)parsedSize;
        typeFlag = buffer[156];
        return buffer;
    }

    private void ReadExactly(byte[] buffer, int count, string errorMessage)
    {
        int done = 0;
        while (done < count)
        {
            int read = _stream.Read(buffer, done, count - done);
            if (read <= 0)
            {
                throw new IOException(errorMessage);
            }
            done += read;
        }
    }

    /// <summary>The data of an extension record ('L', 'K', 'x', 'g') without the padding to whole blocks.</summary>
    private byte[] ReadExtensionData(ulong size)
    {
        if (size > MaxExtensionHeaderSize)
        {
            throw new InvalidDataException("An extension header of the TAR archive is too big");
        }
        byte[] data = new byte[(int)size];
        ReadExactly(data, data.Length, "Incomplete TAR extension header");
        int padLen = (512 - (int)(size % 512)) % 512;
        if (padLen != 0)
        {
            ReadExactly(new byte[padLen], padLen, "Incomplete TAR extension header");
        }
        return data;
    }

    /// <summary>The text of a GNU long name or link target record: up to its NUL.</summary>
    private static string ExtensionText(byte[] data)
    {
        int end = Array.IndexOf(data, (byte)0);
        return Encoding.UTF8.GetString(data, 0, end < 0 ? data.Length : end);
    }

    /// <summary>The whole seconds of a pax time, "1700000000.123456789" or "-86400.5"; the fraction is dropped.</summary>
    internal static bool TryParsePaxTime(string value, out long seconds)
    {
        seconds = 0L;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        int dot = value.IndexOf('.');
        return long.TryParse(dot < 0 ? value : value.Substring(0, dot), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seconds);
    }

    private static DateTime UnixTimeToUtc(long seconds)
    {
        return UnixEpoch.AddSeconds(Math.Max(MinUnixSeconds, Math.Min(MaxUnixSeconds, seconds)));
    }

    /// <summary>
    /// True for an entry name that could place a file outside of the extraction root: a rooted name or a ".." segment
    /// (in either slash style). Such a record can only come from a broken or malicious remote side.
    /// </summary>
    internal static bool IsTraversalEntryName(string entryName)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            return false;
        }
        if (entryName[0] == '/' || entryName[0] == '\\')
        {
            return true;
        }
        foreach (string segment in entryName.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True for a name that exists on Linux but cannot be created on Windows as it is. A ':' would open an NTFS alternate
    /// data stream or a drive-relative path, a segment made of dots and blanks is normalized away by Windows.
    /// </summary>
    internal static bool HasInvalidLocalNameCharacters(string entryName)
    {
        foreach (string segment in entryName.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }
            if (segment.TrimEnd('.', ' ').Length == 0)
            {
                return true;
            }
            foreach (char ch in segment)
            {
                if (ch < ' ' || ch == ':' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|')
                {
                    return true;
                }
            }
        }
        return false;
    }

    private bool IsCaseCollision(string filePath)
    {
        bool contains = _caseInsensitiveNames.Contains(filePath) && !_exactNames.Contains(filePath);
        _exactNames.Add(filePath);
        _caseInsensitiveNames.Add(filePath);
        return contains;
    }

    private bool ExtractNextEntry(bool ignoreErrors, bool stripFirstDirectory)
    {
        string longName = null;
        string longLink = null;
        Dictionary<string, string> pax = null;
        byte[] headerBuffer;
        string entryName;
        ulong entrySize;
        byte typeFlag;
        while (true)
        {
            headerBuffer = ReadHeader(out entryName, out entrySize, out typeFlag);
            if (headerBuffer == null)
            {
                return false;
            }
            if (typeFlag != 'L' && typeFlag != 'K' && typeFlag != 'x' && typeFlag != 'g')
            {
                break;
            }
            // Not an entry but a record about the next one: a GNU long name ('L') or long link target ('K'), pax attributes ('x').
            // A pax global header ('g', git archive writes the commit id into it) is about the whole archive: nothing to extract.
            byte[] data = ReadExtensionData(entrySize);
            if (typeFlag == 'L')
            {
                longName = ExtensionText(data);
            }
            else if (typeFlag == 'K')
            {
                longLink = ExtensionText(data);
            }
            else if (typeFlag == 'x')
            {
                if (pax == null)
                {
                    pax = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                foreach (KeyValuePair<string, string> record in TarArchiveReader.ParsePax(data, data.Length))
                {
                    pax[record.Key] = record.Value;
                }
            }
        }
        Encoding utf8 = Encoding.UTF8;
        string paxPath = null;
        string paxLinkPath = null;
        if (pax != null)
        {
            pax.TryGetValue("path", out paxPath);
            pax.TryGetValue("linkpath", out paxLinkPath);
            if (pax.TryGetValue("size", out string paxSize))
            {
                // The size field of the header cannot say more than 8 GiB, or has been cut to what it can say.
                if (!ulong.TryParse(paxSize, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsedPaxSize) || parsedPaxSize > long.MaxValue)
                {
                    throw new InvalidDataException("Invalid size in a TAR extended header");
                }
                entrySize = parsedPaxSize;
            }
        }
        // The name of the header is a truncated copy when a long name or a pax path follows it.
        if (longName != null)
        {
            entryName = longName;
        }
        if (paxPath != null)
        {
            entryName = paxPath;
        }
        if (entryName.Length == 0)
        {
            throw new Exception("Invalid file name in a TAR record");
        }
        long mtimeSeconds = 0L;
        bool hasPaxTime = false;
        if (pax != null && pax.TryGetValue("mtime", out string paxTime))
        {
            hasPaxTime = TryParsePaxTime(paxTime, out mtimeSeconds);
        }
        if (!hasPaxTime && !TarArchiveChecker.TryParseNumber(headerBuffer, 136, 12, out mtimeSeconds))
        {
            // A time field that is no number costs the time stamp of the file, not the download of the folder.
            mtimeSeconds = 0L;
        }
        DateTime lastWriteTimeUtc = UnixTimeToUtc(mtimeSeconds);
        char lastChar = entryName[entryName.Length - 1];
        bool isDirectory = lastChar == '/' || lastChar == '\\' || typeFlag == 53;
        bool isLink = typeFlag == 49 || typeFlag == 50;
        if (longName == null && paxPath == null && headerBuffer[257] == 117 && headerBuffer[258] == 115 && headerBuffer[259] == 116 && headerBuffer[260] == 97 && headerBuffer[261] == 114 && headerBuffer[262] == 0 && headerBuffer[345] != 0)
        {
            string prefix = utf8.GetString(headerBuffer, 345, 155).TrimEnd(default(char));
            if (!prefix.EndsWith("/"))
            {
                prefix += "/";
            }
            if (prefix != "")
            {
                entryName = prefix + entryName;
            }
        }
        if (IsTraversalEntryName(entryName))
        {
            throw new Exception("Invalid file name in a TAR record");
        }
        entryName = entryName.TrimStart('/', '\\');
        if (stripFirstDirectory)
        {
            int slashIndex = entryName.IndexOf('/');
            if (slashIndex != -1)
            {
                entryName = entryName.Substring(slashIndex + 1).TrimStart('/', '\\');
            }
        }
        string fullPath = null;
        if (entryName != "")
        {
            entryName = entryName.Replace('/', '\\');
            PathRewriting?.Invoke(_rootPath, ref entryName);
            if (IsTraversalEntryName(entryName))
            {
                throw new Exception("Invalid file name in a TAR record");
            }
            if (!HasInvalidLocalNameCharacters(entryName))
            {
                fullPath = Path.Combine(_rootPath, entryName);
            }
        }
        if (IsCaseCollision(fullPath))
        {
            fullPath = null;
        }
        Progress?.Invoke(isSymlink: false, fullPath, (long)entrySize, 0L, _statistics.TotalFiles, _statistics.TotalBytes);
        if (isDirectory)
        {
            if (fullPath != null)
            {
                _statistics.TotalDirectories++;
                if (entrySize != 0L)
                {
                    throw new Exception("Found a TAR directory record with non-zero data size:" + fullPath);
                }
                if (_target != null)
                {
                    _target.CreateDirectory(fullPath, lastWriteTimeUtc);
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(fullPath);
                    }
                    catch (Exception exception)
                    {
                        if (Log != null)
                        {
                            Log(this, $"Cannot create {fullPath}: {exception.Message}");
                        }
                        bool currentIgnoreErrors = ignoreErrors;
                        if (exception is PathTooLongException)
                        {
                            PathTooLongEventArgs pathTooLongEventArgs = new PathTooLongEventArgs
                            {
                                Path = fullPath,
                                IsDirectory = true
                            };
                            PathTooLong?.Invoke(this, pathTooLongEventArgs);
                            if (pathTooLongEventArgs.Handled)
                            {
                                _longPathMode = true;
                                currentIgnoreErrors = true;
                            }
                        }
                        if (!currentIgnoreErrors)
                        {
                            throw;
                        }
                    }
                    if (PreserveTimestamps)
                    {
                        try
                        {
                            Directory.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        else if (isLink)
        {
            if (fullPath != null)
            {
                _statistics.TotalSymlinks++;
                string targetName = paxLinkPath ?? longLink ?? utf8.GetString(headerBuffer, 157, 100).TrimEnd(default(char));
                _symlinks.Add(new PendingSymlink
                {
                    Path = fullPath,
                    Target = targetName,
                    RelativePath = entryName.Replace('\\', '/'),
                    IsHardLink = (typeFlag == 49)
                });
                if (PreserveTimestamps)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
                    }
                    catch
                    {
                    }
                }
            }
        }
        else
        {
            Stream outStream = null;
            try
            {
                if (fullPath != null && fullPath.Contains("?"))
                {
                    fullPath = null;
                }
                if (fullPath != null && FileOverwriting != null && File.Exists(fullPath))
                {
                    bool overwrite = false;
                    FileOverwriting(fullPath, ref overwrite);
                    if (overwrite)
                    {
                        fullPath = null;
                    }
                }
                if (fullPath != null)
                {
                    if (_target != null)
                    {
                        outStream = _target.CreateFile(fullPath);
                    }
                    else
                    {
                        try
                        {
                            outStream = File.Create(fullPath);
                        }
                        catch (DirectoryNotFoundException)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                            outStream = File.Create(fullPath);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                if (Log != null)
                {
                    Log(this, $"Cannot create {fullPath}: {exception.Message}");
                }
                bool currentIgnoreErrors = ignoreErrors;
                if (exception is PathTooLongException || (_longPathMode && exception is DirectoryNotFoundException))
                {
                    PathTooLongEventArgs pathTooLongEventArgs = new PathTooLongEventArgs
                    {
                        Path = fullPath
                    };
                    PathTooLong?.Invoke(this, pathTooLongEventArgs);
                    if (pathTooLongEventArgs.Handled)
                    {
                        currentIgnoreErrors = true;
                    }
                }
                if (!currentIgnoreErrors)
                {
                    throw;
                }
            }
            using (outStream)
            {
                CopyEntryBody(_stream, outStream, (long)entrySize);
            }
            _statistics.TotalFiles++;
            _statistics.TotalBytes += (long)entrySize;
            int padLen = 512 - (int)(entrySize % 512);
            if (padLen != 512)
            {
                _stream.Read(headerBuffer, 0, padLen);
            }
            if (PreserveTimestamps)
            {
                try
                {
                    if (fullPath != null)
                    {
                        File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc);
                    }
                }
                catch
                {
                }
            }
        }
        return true;
    }

    private void CopyEntryBody(Stream sourceStream, Stream targetStream, long length)
    {
        long bytesCopied = 0L;
        while (bytesCopied < length)
        {
            int bytesToRead = (int)Math.Min(length - bytesCopied, _buffer.Length);
            if (sourceStream.Read(_buffer, 0, bytesToRead) != bytesToRead)
            {
                throw new IOException("Cannot read TAR archive body");
            }
            targetStream?.Write(_buffer, 0, bytesToRead);
            bytesCopied += bytesToRead;
            if (Progress != null)
            {
                Progress(isSymlink: false, null, length, bytesCopied, _statistics.TotalFiles, _statistics.TotalBytes);
            }
        }
    }

    public void ExtractAll(bool ignoreErrors, bool stripFirstDirectory)
    {
        int count = 0;
        while (ExtractNextEntry(ignoreErrors, stripFirstDirectory))
        {
            count++;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetFileAttributes(string path);

    internal static int CountParentReferences(string path, bool isForward)
    {
        int maxParentCount = 0;
        int currentDepth = 0;
        // A backslash is a separator on the Windows side where the link is created, so "..\\.." must count as two levels.
        string[] segments = path.Split('/', '\\');
        foreach (string segment in segments)
        {
            // An empty segment (a doubled slash) and "." stay where they are; only a name goes down and ".." goes up.
            if (!(segment == "") && segment != ".")
            {
                currentDepth = ((!(segment == "..")) ? (currentDepth - 1) : (currentDepth + 1));
                maxParentCount = ((!isForward) ? Math.Max(maxParentCount, -currentDepth) : Math.Max(maxParentCount, currentDepth));
            }
        }
        return maxParentCount;
    }

    /// <summary>
    /// Converts an absolute link target that points inside the downloaded remote directory into a path relative to the
    /// link (a symlink) or to the extraction root (a hard link). Returns null for a target outside of remoteRoot.
    /// </summary>
    internal static string MakeRelativeToRoot(string relativeLinkPath, string targetPath, string remoteRoot, bool isHardLink)
    {
        string root = remoteRoot.TrimEnd('/');
        string rest;
        if (targetPath.TrimEnd('/') == root)
        {
            rest = "";
        }
        else if (targetPath.StartsWith(root + "/", StringComparison.Ordinal))
        {
            rest = targetPath.Substring(root.Length + 1);
        }
        else
        {
            return null;
        }
        // Only the start of the target was compared with the root. What follows is a path of its own and may climb out of the
        // root with ".." (/srv/proj/../../x) or be rooted on Windows (a drive letter): it gets the checks of a relative target.
        if (CountParentReferences(rest, isForward: true) > 0 || !IsPlainRelativeTarget(rest))
        {
            return null;
        }
        string result = "";
        if (!isHardLink)
        {
            // The last segment is the link itself, only the directories above it have to be left.
            int levelsUp = CountParentReferences(relativeLinkPath, isForward: false) - 1;
            for (int i = 0; i < levelsUp; i++)
            {
                result += "../";
            }
        }
        result += rest;
        return result.Length == 0 ? "." : result.Replace('/', '\\');
    }

    /// <summary>
    /// True for a link target that stays a plain relative path on Windows: it is not rooted (a leading slash or backslash, a drive
    /// letter or a UNC path would replace the folder it is combined with) and its segments are ordinary names, "." and "..".
    /// </summary>
    internal static bool IsPlainRelativeTarget(string target)
    {
        if (target.Length != 0 && (target[0] == '/' || target[0] == '\\'))
        {
            return false;
        }
        foreach (string segment in target.Split('/', '\\'))
        {
            if (segment == "" || segment == "." || segment == "..")
            {
                continue;
            }
            // A ':' is a drive or a data stream, a name of dots and blanks is normalized away: the rules of the names of entries.
            if (HasInvalidLocalNameCharacters(segment))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True when a link target, relative to the link (a symlink) or to the extraction root (a hard link), cannot lead out of the
    /// extraction root. A link outside would let a later download write through it into a foreign place.
    /// </summary>
    internal static bool IsTargetInsideRoot(string relativeLinkPath, string target, bool isHardLink)
    {
        if (!IsPlainRelativeTarget(target))
        {
            return false;
        }
        int targetParents = CountParentReferences(target, isForward: true);
        int pathParents = CountParentReferences(relativeLinkPath, isForward: false) - 1;
        // A hard link target is relative to the archive root, so it has no business going up.
        return isHardLink ? targetParents == 0 : targetParents <= pathParents;
    }

    /// <summary>
    /// The target of a link as a path relative to the link (a symlink) or to the extraction root (a hard link), or null when the
    /// link cannot be created: its target is outside of the download folder (remoteRoot is the folder on the server, it is needed
    /// to make sense of an absolute target) or is no plain relative path. Absolute and relative targets get the same check.
    /// </summary>
    internal static string ResolveRelativeTarget(string relativeLinkPath, string target, bool isHardLink, string remoteRoot)
    {
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }
        string relativeTarget;
        if (target[0] == '/')
        {
            if (remoteRoot == null)
            {
                return null;
            }
            relativeTarget = MakeRelativeToRoot(relativeLinkPath, target, remoteRoot, isHardLink);
        }
        else
        {
            relativeTarget = target.Replace('/', '\\');
        }
        return relativeTarget != null && IsTargetInsideRoot(relativeLinkPath, relativeTarget, isHardLink) ? relativeTarget : null;
    }

    public void CreateSymlinks(out List<string> unresolvedLinks, string remoteRoot = null)
    {
        unresolvedLinks = new List<string>();
        List<ResolvedLink> resolvedList = new List<ResolvedLink>();
        foreach (PendingSymlink item in _symlinks)
        {
            if (string.IsNullOrEmpty(item.Target))
            {
                continue;
            }
            string relativeTarget = ResolveRelativeTarget(item.RelativePath, item.Target, item.IsHardLink, remoteRoot);
            if (relativeTarget != null)
            {
                string baseDir = null;
                if (item.IsHardLink)
                {
                    baseDir = _rootPath;
                }
                resolvedList.Add(new ResolvedLink(item.Path, relativeTarget, item.RelativePath, baseDir));
            }
            else
            {
                unresolvedLinks.Add(item.RelativePath);
            }
        }
        _symlinks.Clear();
        bool anyCreated;
        do
        {
            anyCreated = false;
            foreach (ResolvedLink link in resolvedList)
            {
                if (link.Created)
                {
                    continue;
                }
                int attributes = GetFileAttributes(link.FullTargetPath);
                if (attributes != -1)
                {
                    if (Progress != null)
                    {
                        Progress(isSymlink: true, link.LinkPath, 0L, 0L, 0, 0L);
                    }
                    if (CreateLink(link.LinkPath, link.Target, link.FullTargetPath, (attributes & 0x10) != 0))
                    {
                        anyCreated = true;
                        link.Created = true;
                    }
                }
            }
        }
        while (anyCreated);
        foreach (ResolvedLink link in resolvedList)
        {
            if (!link.Created)
            {
                unresolvedLinks.Add(link.OriginalName);
            }
        }
    }

    private static void RetryFor(ThreadStart callback, int timeoutMs = 5000)
    {
        try
        {
            callback();
        }
        catch
        {
            DateTime now = DateTime.Now;
            while (true)
            {
                try
                {
                    Thread.Sleep(100);
                    callback();
                    break;
                }
                catch
                {
                    if ((DateTime.Now - now).TotalMilliseconds > (double)timeoutMs)
                    {
                        throw;
                    }
                }
            }
        }
    }

    public static void DeleteDirectoryRecursive(string dirPath)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(dirPath);
        }
        catch
        {
            Directory.Delete(dirPath);
            return;
        }
        foreach (string subDir in directories)
        {
            DeleteDirectoryRecursive(subDir);
        }
        string[] files = Directory.GetFiles(dirPath);
        foreach (string file in files)
        {
            if (File.Exists(file))
            {
                RetryFor(() =>
                {
                    File.Delete(file);
                });
            }
        }
        if (Directory.Exists(dirPath))
        {
            RetryFor(() =>
            {
                Directory.Delete(dirPath, recursive: true);
            });
        }
    }

    public void AddStatisticsTo(DirectoryStatistics directoryStatistics)
    {
        if (directoryStatistics != null)
        {
            directoryStatistics.TotalBytes += _statistics.TotalBytes;
            directoryStatistics.TotalDirectories += _statistics.TotalDirectories;
            directoryStatistics.TotalFiles += _statistics.TotalFiles;
            directoryStatistics.TotalSymlinks += _statistics.TotalSymlinks;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static void CopyDirectory(string sourceDir, string destDir, bool copySubDirs)
    {
        DirectoryInfo directoryInfo = new DirectoryInfo(sourceDir);
        DirectoryInfo[] directories = directoryInfo.GetDirectories();
        if (!directoryInfo.Exists)
        {
            throw new DirectoryNotFoundException("Source directory does not exist or could not be found: " + sourceDir);
        }
        if (!Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }
        FileInfo[] files = directoryInfo.GetFiles();
        foreach (FileInfo fileInfo in files)
        {
            string destFileName = Path.Combine(destDir, fileInfo.Name);
            fileInfo.CopyTo(destFileName, overwrite: true);
        }
        if (copySubDirs)
        {
            foreach (DirectoryInfo subDirInfo in directories)
            {
                string destSubDir = Path.Combine(destDir, subDirInfo.Name);
                CopyDirectory(subDirInfo.FullName, destSubDir, copySubDirs);
            }
        }
    }

    private bool CreateLink(string linkPath, string targetPath, string fullTargetPath, bool isDirectory)
    {
        if (GetFileAttributes(linkPath) != -1 && !isDirectory)
        {
            try
            {
                File.Delete(linkPath);
            }
            catch
            {
            }
        }
        if (isDirectory)
        {
            try
            {
                try
                {
                    if (Directory.Exists(linkPath))
                    {
                        try
                        {
                            _ = ReparsePoints.ReadReparsePoint(linkPath).Value;
                        }
                        catch
                        {
                        }
                    }
                    ReparsePoints.CreateJunction(linkPath, fullTargetPath);
                }
                catch
                {
                    CopyDirectory(fullTargetPath, linkPath, copySubDirs: true);
                }
            }
            catch
            {
                return false;
            }
        }
        else if (!CreateHardLink(linkPath, fullTargetPath, IntPtr.Zero))
        {
            try
            {
                File.Copy(fullTargetPath, linkPath);
            }
            catch
            {
                return false;
            }
        }
        return true;
    }
}
