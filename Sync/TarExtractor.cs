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

    public delegate void TarProgressHandler(bool flag, string text, long value2, long value3, int value4, long value5);

    public delegate void PathRewriteHandler(string text, ref string text2);

    public delegate void OverwriteHandler(string text, ref bool flag);

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

        public ResolvedLink(string text, string text2, string text3, string text4)
        {
            LinkPath = text;
            this.Target = text2;
            this.FullTargetPath = Path.Combine(text4 ?? Path.GetDirectoryName(text), text2);
            this.OriginalName = text3;
        }
    }

    private Stream _stream;

    private string _rootPath;

    private readonly IExtractionTarget _target;

    private byte[] _buffer = new byte[1048576];

    private static DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

    private long _bytesRead;

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

    public TarExtractor(Stream stream, string text, IExtractionTarget extractionTarget = null)
    {
        _stream = stream;
        _rootPath = text;
        _target = extractionTarget;
    }

    private byte[] ReadHeader(out string text, out ulong value2, out byte b2)
    {
        byte[] array = new byte[512];
        int num = _stream.Read(array, 0, 512);
        if (num == 0 || array[0] == 0)
        {
            text = null;
            value2 = 0uL;
            b2 = 0;
            return null;
        }
        Encoding uTF = Encoding.UTF8;
        _bytesRead += num;
        if (num != 512)
        {
            throw new IOException("Incomplete TAR header");
        }
        text = uTF.GetString(array, 0, 100).TrimEnd(default(char));
        if (text == "")
        {
            throw new Exception("Invalid file name in a TAR record");
        }
        value2 = Convert.ToUInt64(uTF.GetString(array, 124, 12).TrimEnd('\0', ' '), 8);
        b2 = array[156];
        return array;
    }

    private bool IsCaseCollision(string text)
    {
        bool result = _caseInsensitiveNames.Contains(text) && !_exactNames.Contains(text);
        _exactNames.Add(text);
        _caseInsensitiveNames.Add(text);
        return result;
    }

    private bool ExtractNextEntry(bool flag6, bool flag7)
    {
        byte[] array = ReadHeader(out var text, out var num, out var b2);
        if (array == null)
        {
            return false;
        }
        Encoding uTF = Encoding.UTF8;
        if (b2 == 76 && text == "././@LongLink")
        {
            int num2 = 512 - (int)(num % 512);
            byte[] array2 = new byte[num];
            if (_stream.Read(array2, 0, (int)num) != (int)num)
            {
                throw new IOException("Incomplete long TAR name");
            }
            if (num2 != 512)
            {
                _stream.Read(array, 0, num2);
            }
            array = ReadHeader(out text, out num, out b2);
            if (array == null)
            {
                return false;
            }
            text = uTF.GetString(array2, 0, array2.Length).TrimEnd(default(char));
        }
        ulong num3 = Convert.ToUInt64(uTF.GetString(array, 136, 12).TrimEnd('\0', ' '), 8);
        DateTime lastWriteTimeUtc = UnixEpoch.AddSeconds(num3);
        char ch = text[text.Length - 1];
        bool flag = ch == '/' || ch == '\\' || b2 == 53;
        bool flag2 = b2 == 49 || b2 == 50;
        if (array[257] == 117 && array[258] == 115 && array[259] == 116 && array[260] == 97 && array[261] == 114 && array[345] != 0)
        {
            string text2 = uTF.GetString(array, 345, 155).TrimEnd(default(char));
            if (!text2.EndsWith("/"))
            {
                text2 += "/";
            }
            if (text2 != "")
            {
                text = text2 + text;
            }
        }
        if (text.StartsWith("..") || text.StartsWith("\\") || text.StartsWith("/"))
        {
            throw new Exception("Invalid file name in a TAR record");
        }
        text = text.TrimStart('/', '\\');
        if (flag7)
        {
            int num4 = text.IndexOf('/');
            if (num4 != -1)
            {
                text = text.Substring(num4 + 1).TrimStart('/', '\\');
            }
        }
        string text3 = null;
        if (text != "")
        {
            text = text.Replace('/', '\\');
            PathRewriting?.Invoke(_rootPath, ref text);
            text3 = Path.Combine(_rootPath, text);
        }
        if (IsCaseCollision(text3))
        {
            text3 = null;
        }
        Progress?.Invoke(flag: false, text3, (long)num, 0L, _statistics.TotalFiles, _statistics.TotalBytes);
        if (flag)
        {
            if (text3 != null)
            {
                _statistics.TotalDirectories++;
                if (num != 0L)
                {
                    throw new Exception("Found a TAR directory record with non-zero data size:" + text3);
                }
                if (_target != null)
                {
                    _target.CreateDirectory(text3, lastWriteTimeUtc);
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(text3);
                    }
                    catch (Exception ex)
                    {
                        if (Log != null)
                        {
                            Log(this, $"Cannot create {text3}: {ex.Message}");
                        }
                        bool flag3 = flag6;
                        if (ex is PathTooLongException)
                        {
                            PathTooLongEventArgs pathTooLongEventArgs = new PathTooLongEventArgs
                            {
                                Path = text3,
                                IsDirectory = true
                            };
                            PathTooLong?.Invoke(this, pathTooLongEventArgs);
                            if (pathTooLongEventArgs.Handled)
                            {
                                _longPathMode = true;
                                flag3 = true;
                            }
                        }
                        if (!flag3)
                        {
                            throw;
                        }
                    }
                    if (PreserveTimestamps)
                    {
                        try
                        {
                            Directory.SetLastWriteTimeUtc(text3, lastWriteTimeUtc);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        else if (flag2)
        {
            if (text3 != null)
            {
                _statistics.TotalSymlinks++;
                string text4 = uTF.GetString(array, 157, 100).TrimEnd(default(char));
                _symlinks.Add(new PendingSymlink
                {
                    Path = text3,
                    Target = text4,
                    RelativePath = text.Replace('\\', '/'),
                    IsHardLink = (b2 == 49)
                });
                if (PreserveTimestamps)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(text3, lastWriteTimeUtc);
                    }
                    catch
                    {
                    }
                }
            }
        }
        else
        {
            Stream stream = null;
            try
            {
                if (text3 != null && text3.Contains("?"))
                {
                    text3 = null;
                }
                if (text3 != null && FileOverwriting != null && File.Exists(text3))
                {
                    bool flag4 = false;
                    FileOverwriting(text3, ref flag4);
                    if (flag4)
                    {
                        text3 = null;
                    }
                }
                if (text3 != null)
                {
                    if (_target != null)
                    {
                        stream = _target.CreateFile(text3);
                    }
                    else
                    {
                        try
                        {
                            stream = File.Create(text3);
                        }
                        catch (DirectoryNotFoundException)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(text3));
                            stream = File.Create(text3);
                        }
                    }
                }
            }
            catch (Exception ex3)
            {
                if (Log != null)
                {
                    Log(this, $"Cannot create {text3}: {ex3.Message}");
                }
                bool flag5 = flag6;
                if (ex3 is PathTooLongException || (_longPathMode && ex3 is DirectoryNotFoundException))
                {
                    PathTooLongEventArgs pathTooLongEventArgs2 = new PathTooLongEventArgs
                    {
                        Path = text3
                    };
                    PathTooLong?.Invoke(this, pathTooLongEventArgs2);
                    if (pathTooLongEventArgs2.Handled)
                    {
                        flag5 = true;
                    }
                }
                if (!flag5)
                {
                    throw;
                }
            }
            using (stream)
            {
                CopyEntryBody(_stream, stream, (long)num);
            }
            _statistics.TotalFiles++;
            _statistics.TotalBytes += (long)num;
            _bytesRead += (long)num;
            int num5 = 512 - (int)(num % 512);
            if (num5 != 512)
            {
                _stream.Read(array, 0, num5);
            }
            _bytesRead += num5;
            if (PreserveTimestamps)
            {
                try
                {
                    if (text3 != null)
                    {
                        File.SetLastWriteTimeUtc(text3, lastWriteTimeUtc);
                    }
                }
                catch
                {
                }
            }
        }
        return true;
    }

    private void CopyEntryBody(Stream stream, Stream stream2, long value2)
    {
        long num = 0L;
        while (num < value2)
        {
            int num2 = (int)Math.Min(value2 - num, _buffer.Length);
            if (stream.Read(_buffer, 0, num2) != num2)
            {
                throw new IOException("Cannot read TAR archive body");
            }
            stream2?.Write(_buffer, 0, num2);
            num += num2;
            if (Progress != null)
            {
                Progress(flag: false, null, value2, num, _statistics.TotalFiles, _statistics.TotalBytes);
            }
        }
    }

    public void ExtractAll(bool flag, bool flag2)
    {
        int num = 0;
        while (ExtractNextEntry(flag, flag2))
        {
            num++;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetFileAttributes(string text);

    private static string ResolvePath(string text2, string text3)
    {
        Stack<string> stack = new Stack<string>(text2.Split(new char[1] { '/' }, StringSplitOptions.RemoveEmptyEntries));
        string[] array = text3.Split('/');
        foreach (string text in array)
        {
            if (!(text == ""))
            {
                if (text == ".." && stack.Count > 0)
                {
                    stack.Pop();
                }
                stack.Push(text);
            }
        }
        return "/" + string.Join("/", stack.ToArray());
    }

    private int CountParentReferences(string text2, bool flag)
    {
        int num = 0;
        int num2 = 0;
        string[] array = text2.Split('/');
        foreach (string text in array)
        {
            if (!(text == ""))
            {
                num2 = ((!(text == "..")) ? (num2 - 1) : (num2 + 1));
                num = ((!flag) ? Math.Max(num, -num2) : Math.Max(num, num2));
            }
        }
        return num;
    }

    public void CreateSymlinks(out List<string> list2, string text4 = null)
    {
        list2 = new List<string>();
        List<ResolvedLink> list = new List<ResolvedLink>();
        foreach (PendingSymlink item in _symlinks)
        {
            if (string.IsNullOrEmpty(item.Target))
            {
                continue;
            }
            string text = null;
            if (item.Target[0] == '/')
            {
                string text2 = item.Target;
                if (text4 != null && text2.StartsWith(text4))
                {
                    int num = CountParentReferences(item.RelativePath, flag: false);
                    for (int num2 = 0; num2 < num; num2++)
                    {
                        text += "../";
                    }
                    text += text2.Substring(text4.TrimEnd('/').Length + 1);
                    text = text.Replace('/', '\\');
                }
            }
            else
            {
                int num3 = CountParentReferences(item.Target, flag: true);
                int num4 = CountParentReferences(item.RelativePath, flag: false) - 1;
                if (num3 <= num4)
                {
                    text = item.Target.Replace('/', '\\');
                }
            }
            if (text != null)
            {
                string text3 = null;
                if (item.IsHardLink)
                {
                    text3 = _rootPath;
                }
                list.Add(new ResolvedLink(item.Path, text, item.RelativePath, text3));
            }
            else
            {
                list2.Add(item.RelativePath);
            }
        }
        _symlinks.Clear();
        bool flag;
        do
        {
            flag = false;
            foreach (ResolvedLink item2 in list)
            {
                if (item2.Created)
                {
                    continue;
                }
                int fileAttributes = GetFileAttributes(item2.FullTargetPath);
                if (fileAttributes != -1)
                {
                    if (Progress != null)
                    {
                        Progress(flag: true, item2.LinkPath, 0L, 0L, 0, 0L);
                    }
                    if (CreateLink(item2.LinkPath, item2.Target, item2.FullTargetPath, (fileAttributes & 0x10) != 0))
                    {
                        flag = true;
                        item2.Created = true;
                    }
                }
            }
        }
        while (flag);
        foreach (ResolvedLink item3 in list)
        {
            if (!item3.Created)
            {
                list2.Add(item3.OriginalName);
            }
        }
    }

    public void SaveStatistics(string text)
    {
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(DirectoryStatistics));
        using FileStream stream = File.Create(text);
        xmlSerializer.Serialize(stream, _statistics);
    }

    private static void RetryFor(ThreadStart callback, int value2 = 5000)
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
                    if ((DateTime.Now - now).TotalMilliseconds > (double)value2)
                    {
                        throw;
                    }
                }
            }
        }
    }

    public static void DeleteDirectoryRecursive(string text)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(text);
        }
        catch
        {
            Directory.Delete(text);
            return;
        }
        string[] array = directories;
        for (int num = 0; num < array.Length; num++)
        {
            DeleteDirectoryRecursive(array[num]);
        }
        array = Directory.GetFiles(text);
        foreach (string text2 in array)
        {
            if (File.Exists(text2))
            {
                RetryFor(() =>
                {
                    File.Delete(text2);
                });
            }
        }
        if (Directory.Exists(text))
        {
            RetryFor(() =>
            {
                Directory.Delete(text, recursive: true);
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
    private static extern bool CreateHardLink(string text, string text2, IntPtr handle);

    private static void CopyDirectory(string text2, string text3, bool flag)
    {
        DirectoryInfo directoryInfo = new DirectoryInfo(text2);
        DirectoryInfo[] directories = directoryInfo.GetDirectories();
        if (!directoryInfo.Exists)
        {
            throw new DirectoryNotFoundException("Source directory does not exist or could not be found: " + text2);
        }
        if (!Directory.Exists(text3))
        {
            Directory.CreateDirectory(text3);
        }
        FileInfo[] files = directoryInfo.GetFiles();
        foreach (FileInfo fileInfo in files)
        {
            string destFileName = Path.Combine(text3, fileInfo.Name);
            fileInfo.CopyTo(destFileName, overwrite: true);
        }
        if (flag)
        {
            DirectoryInfo[] array = directories;
            foreach (DirectoryInfo directoryInfo2 in array)
            {
                string text = Path.Combine(text3, directoryInfo2.Name);
                CopyDirectory(directoryInfo2.FullName, text, flag);
            }
        }
    }

    private bool CreateLink(string text, string text2, string text3, bool flag)
    {
        if (GetFileAttributes(text) != -1 && !flag)
        {
            try
            {
                File.Delete(text);
            }
            catch
            {
            }
        }
        if (flag)
        {
            try
            {
                try
                {
                    if (Directory.Exists(text))
                    {
                        try
                        {
                            _ = ReparsePoints.ReadReparsePoint(text).Value;
                        }
                        catch
                        {
                        }
                    }
                    ReparsePoints.CreateJunction(text, text3);
                }
                catch
                {
                    CopyDirectory(text3, text, flag: true);
                }
            }
            catch
            {
                return false;
            }
        }
        else if (!CreateHardLink(text, text3, IntPtr.Zero))
        {
            try
            {
                File.Copy(text3, text);
            }
            catch
            {
                return false;
            }
        }
        return true;
    }
}
