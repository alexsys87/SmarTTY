using SmarTTY.Native;
using System;
using System.Collections.Generic;
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
        size = Convert.ToUInt64(utf8.GetString(buffer, 124, 12).TrimEnd('\0', ' '), 8);
        typeFlag = buffer[156];
        return buffer;
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
        byte[] headerBuffer = ReadHeader(out var entryName, out var entrySize, out var typeFlag);
        if (headerBuffer == null)
        {
            return false;
        }
        Encoding utf8 = Encoding.UTF8;
        if (typeFlag == 76 && entryName == "././@LongLink")
        {
            int padLen = 512 - (int)(entrySize % 512);
            byte[] nameBuffer = new byte[entrySize];
            if (_stream.Read(nameBuffer, 0, (int)entrySize) != (int)entrySize)
            {
                throw new IOException("Incomplete long TAR name");
            }
            if (padLen != 512)
            {
                _stream.Read(headerBuffer, 0, padLen);
            }
            headerBuffer = ReadHeader(out entryName, out entrySize, out typeFlag);
            if (headerBuffer == null)
            {
                return false;
            }
            entryName = utf8.GetString(nameBuffer, 0, nameBuffer.Length).TrimEnd(default(char));
        }
        ulong mtimeSec = Convert.ToUInt64(utf8.GetString(headerBuffer, 136, 12).TrimEnd('\0', ' '), 8);
        DateTime lastWriteTimeUtc = UnixEpoch.AddSeconds(mtimeSec);
        char lastChar = entryName[entryName.Length - 1];
        bool isDirectory = lastChar == '/' || lastChar == '\\' || typeFlag == 53;
        bool isLink = typeFlag == 49 || typeFlag == 50;
        if (headerBuffer[257] == 117 && headerBuffer[258] == 115 && headerBuffer[259] == 116 && headerBuffer[260] == 97 && headerBuffer[261] == 114 && headerBuffer[262] == 0 && headerBuffer[345] != 0)
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
                string targetName = utf8.GetString(headerBuffer, 157, 100).TrimEnd(default(char));
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
            if (!(segment == ""))
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
            string relativeTarget = null;
            if (item.Target[0] == '/')
            {
                if (remoteRoot != null)
                {
                    relativeTarget = MakeRelativeToRoot(item.RelativePath, item.Target, remoteRoot, item.IsHardLink);
                }
            }
            else
            {
                int targetParents = CountParentReferences(item.Target, isForward: true);
                int pathParents = CountParentReferences(item.RelativePath, isForward: false) - 1;
                // A hard link target is relative to the archive root, so it has no business going up.
                if (item.IsHardLink ? targetParents == 0 : targetParents <= pathParents)
                {
                    relativeTarget = item.Target.Replace('/', '\\');
                }
            }
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

    public void SaveStatistics(string filePath)
    {
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(DirectoryStatistics));
        using FileStream stream = File.Create(filePath);
        xmlSerializer.Serialize(stream, _statistics);
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
