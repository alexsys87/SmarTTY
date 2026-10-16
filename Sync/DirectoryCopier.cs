using ManagedLibSSH2;
using ManagedLibSSH2.QuickSync;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace SmarTTY.Sync;

public class DirectoryCopier
{
    public static class TarCommands
    {
        public static string GetExtractCommand(string text, bool flag)
        {
            if (flag)
            {
                return $"cd \"{text}\" && tar -xmf -";
            }
            return $"cd \"{text}\" && tar -xf -";
        }

        public static string GetCreateCommand(string text2, bool flag, string text3 = "*", string[] items = null)
        {
            string text = (flag ? "h" : "");
            StringBuilder stringBuilder = new StringBuilder();
            if (items != null)
            {
                foreach (string arg in items)
                {
                    stringBuilder.AppendFormat(" --exclude='{0}'", arg);
                }
            }
            return $"cd \"{text2}\" && tar{stringBuilder} -c{text}f - {text3}";
        }

        public static string GetCreateFromListCommand(string text2, bool flag, string text3, string[] items = null)
        {
            string text = (flag ? "h" : "");
            StringBuilder stringBuilder = new StringBuilder();
            if (items != null)
            {
                foreach (string arg in items)
                {
                    stringBuilder.AppendFormat(" --exclude='{0}'", arg);
                }
            }
            return $"cd \"{text2}\" && tar{stringBuilder} -c{text}T {text3} -f -";
        }
    }

    private class OutputCollector
    {
        private byte[] _data = new byte[0];

        public void OnDataReceived(object obj, byte[] buffer, int value2, int value3, int value4)
        {
            int num = this._data.Length;
            Array.Resize(ref this._data, num + value3);
            Array.Copy(buffer, value2, this._data, num, value3);
        }

        internal MemoryStream GetStream()
        {
            if (_data.Length == 0)
            {
                return null;
            }
            return new MemoryStream(_data);
        }

        internal void Clear()
        {
            _data = new byte[0];
        }
    }

    public enum CopyStage
    {
        Counting,
        CopyingFile,
        CopyingTarEntry,
        StreamingTar,
        Completed
    }

    public struct CopyProgress(CopyStage copyStage, string text, int value2, int value3, long value4, long value5)
    {
        public readonly CopyStage Stage = copyStage;

        public readonly string Path = text;

        public readonly int FilesDone = value2;

        public readonly int FilesTotal = value3;

        public readonly long BytesDone = value4;

        public readonly long BytesTotal = value5;
    }

    public delegate void ProgressHandler(CopyProgress copyProgress);

    public delegate bool PathFilter(string text);

    private class DirectoryEntry
    {
        public string Name;

        public DateTime Time;
    }

    private SshSession _session;

    private string _tempPath;

    private int _fileCount;

    private long _byteCount;

    public bool UseTempFileList = true;

    public bool FollowSymlinks;

    public bool UseTar = true;

    public Func<string, SshShellChannel> ChannelFactory;

    public bool CountFirst = true;

    private byte[] _buffer;

    private long _bytesDone;

    public event ProgressHandler Progress;

    public event TarExtractor.OverwriteHandler TarOverwrite;

    public event Action<object, string> Log;

    public DirectoryCopier(SshSession sshSession, string text = "/tmp")
    {
        _session = sshSession;
        _tempPath = text;
    }

    public void Download(string text, string text2, string text3 = null, string[] items = null, bool flag = false)
    {
        Directory.CreateDirectory(text2);
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress(CopyStage.Counting, null, 0, 0, 0L, 0L));
        }
        Regex[] array = null;
        if (items != null)
        {
            array = new Regex[items.Length];
            for (int num = 0; num < items.Length; num++)
            {
                array[num] = WildcardParser.WildcardToRegex(items[num]);
            }
        }
        if (!CountFirst || !CountRemote(text, out _fileCount, out _byteCount, array))
        {
            _fileCount = 0;
            _byteCount = 0L;
        }
        if (UseTar)
        {
            DownloadViaTar(text, text2, text3, items, flag);
            return;
        }
        long totalBytes = 0L;
        int totalFiles = 0;
        DownloadFileByFile(text, text2, ref totalFiles, ref totalBytes, array);
        if (string.IsNullOrEmpty(text3))
        {
            return;
        }
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(DirectoryStatistics));
        using FileStream stream = File.Create(text3);
        xmlSerializer.Serialize(stream, new DirectoryStatistics
        {
            TotalBytes = totalBytes,
            TotalFiles = totalFiles
        });
    }

    public void DownloadViaTar(string text2, string text3, string text4, string[] items, bool flag = false, string text5 = "*")
    {
        OutputCollector outputCollector = new OutputCollector();
        string text = TarCommands.GetCreateCommand(text2, FollowSymlinks, text5, items);
        DirectoryStatistics directoryStatistics = ((text4 == null) ? null : new DirectoryStatistics());
        List<string> list;
        using (SshShellChannel sshShellChannel = this._session.OpenShellChannel(text))
        {
            Directory.CreateDirectory(text3);
            sshShellChannel.DataReceived += outputCollector.OnDataReceived;
            sshShellChannel.ReadStderrOnRead = true;
            sshShellChannel.Output.ReadFully = true;
            TarExtractor tarExtractor = new TarExtractor(sshShellChannel.Output, text3)
            {
                PreserveTimestamps = true
            };
            if (TarOverwrite != null)
            {
                tarExtractor.FileOverwriting += TarOverwrite;
            }
            tarExtractor.Log += this.Log;
            tarExtractor.Progress = OnTarProgress;
            tarExtractor.ExtractAll(flag: true, flag2: false);
            tarExtractor.CreateSymlinks(out list, text2);
            tarExtractor.AddStatisticsTo(directoryStatistics);
            CheckErrors(outputCollector);
        }
        if (list != null && list.Count != 0 && UseTempFileList)
        {
            string arg = _tempPath + "/" + $"ssh{DateTime.Now.ToFileTimeUtc()}-{Process.GetCurrentProcess().Id}-{Environment.MachineName}";
            for (int num = 0; num < 1; num++)
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    using StreamWriter streamWriter = new StreamWriter(memoryStream);
                    streamWriter.NewLine = "\n";
                    foreach (string item in list)
                    {
                        streamWriter.WriteLine(item);
                    }
                    streamWriter.Flush();
                    using ScpChannelStream scpChannelStream = this._session.OpenUpload(arg, memoryStream.Length);
                    scpChannelStream.Write(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
                }
                text = TarCommands.GetCreateFromListCommand(text2, flag: true, arg, items);
                using (SshShellChannel sshShellChannel2 = this._session.OpenShellChannel(text))
                {
                    sshShellChannel2.DataReceived += outputCollector.OnDataReceived;
                    sshShellChannel2.ReadStderrOnRead = true;
                    sshShellChannel2.Output.ReadFully = true;
                    TarExtractor tarExtractor2 = new TarExtractor(sshShellChannel2.Output, text3);
                    tarExtractor2.PreserveTimestamps = flag;
                    tarExtractor2.Progress = OnTarListProgress;
                    tarExtractor2.ExtractAll(flag: true, flag2: false);
                    tarExtractor2.CreateSymlinks(out list, text2);
                    tarExtractor2.AddStatisticsTo(directoryStatistics);
                    CheckErrors(outputCollector);
                }
                if (list.Count == 0)
                {
                    break;
                }
            }
            this._session.RunCommandChecked($"rm \"{arg}\"", requireSuccess: false);
        }
        if (!string.IsNullOrEmpty(text4) && directoryStatistics != null)
        {
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(DirectoryStatistics));
            using FileStream stream = File.Create(text4);
            xmlSerializer.Serialize(stream, directoryStatistics);
        }
    }

    private void DownloadFileByFile(string text2, string text3, ref int value2, ref long value3, Regex[] items)
    {
        List<string> list = new List<string>();
        List<string> list2 = new List<string>();
        List<string> list3 = this._session.RunCommandChecked("ls -1ALF \"" + text2 + "\"", requireSuccess: true);
        if (list3 == null)
        {
            if (Log != null)
            {
                Log(this, "Cannot list contents of " + text2);
            }
            return;
        }
        foreach (string item in list3)
        {
            if (!string.IsNullOrEmpty(item))
            {
                if (item[item.Length - 1] == '/')
                {
                    list2.Add(item.Substring(0, item.Length - 1));
                }
                else if (item[item.Length - 1] == '*')
                {
                    list.Add(item.Substring(0, item.Length - 1));
                }
                else if ("@/=>|".IndexOf(item[item.Length - 1]) == -1)
                {
                    list.Add(item);
                }
            }
        }
        Directory.CreateDirectory(text3);
        foreach (string item2 in list)
        {
            string text = Path.Combine(text3, item2);
            if (IsExcluded(item2, items))
            {
                continue;
            }
            try
            {
                this.CountFiles(text2 + "/" + item2, ref value2, ref value3, text);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex2)
            {
                if (Log != null)
                {
                    Log(this, "Cannot retrieve contents of " + text + ": " + ex2.Message);
                }
            }
        }
        foreach (string item3 in list2)
        {
            DownloadFileByFile(text2 + "/" + item3, text3 + "\\" + item3, ref value2, ref value3, items);
        }
    }

    public void CountFiles(string text, ref int value2, ref long value3, string text2)
    {
        if (TarOverwrite != null && File.Exists(text2))
        {
            bool flag = false;
            TarOverwrite(text2, ref flag);
            if (flag)
            {
                return;
            }
        }
        using ScpDownloadStream scpDownloadStream = this._session.OpenDownload(text);
        using FileStream fileStream = File.Create(text2);
        scpDownloadStream.AbortOnShortRead = true;
        CopyStream(scpDownloadStream, fileStream, ref value2, ref value3, text2, -1L);
    }

    private void CopyStream(Stream stream, Stream stream2, ref int value2, ref long value3, string text, long value4 = -1L)
    {
        if (_buffer == null)
        {
            _buffer = new byte[1048576];
        }
        byte[] array = _buffer;
        long num = 0L;
        if (value4 < 0)
        {
            value4 = stream.Length;
        }
        while (num < value4)
        {
            long num2 = Math.Min(array.LongLength, value4 - num);
            int num3 = stream.Read(array, 0, (int)num2);
            if (num3 <= 0)
            {
                break;
            }
            stream2.Write(array, 0, num3);
            num += num3;
            value3 += num3;
            if (this.Progress != null)
            {
                this.Progress(new CopyProgress(CopyStage.CopyingFile, text, value2, _fileCount, value3, _byteCount));
            }
        }
        value2++;
    }

    private void OnTarProgress(bool flag, string text, long value2, long value3, int value4, long value5)
    {
        if (!flag)
        {
            _bytesDone = Math.Max(_bytesDone, value3 + value5);
        }
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress((!flag) ? CopyStage.CopyingFile : CopyStage.CopyingTarEntry, text, value4, _fileCount, value3 + value5, _byteCount));
        }
    }

    private void OnTarListProgress(bool flag, string text, long value2, long value3, int value4, long value5)
    {
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress(CopyStage.StreamingTar, text, 0, 0, _bytesDone + value3 + value5, 0L));
        }
    }

    public bool CountRemote(string text4, out int value2, out long value3, Regex[] items = null)
    {
        OutputCollector outputCollector = new OutputCollector();
        value2 = 0;
        value3 = 0L;
        Regex regex = new Regex("^[^ \t]+[ \t]+[^ \t]+[ \t]+[^ \t]+[ \t]+[^ \t]+[ \t]+([0-9]+)[ \t]+");
        Regex regex2 = new Regex("^[^ \t]+[ \t]+[^ \t]+[ \t]+[^ \t]+[ \t]+([0-9]+)[ \t]+");
        Regex regex3 = new Regex("^[a-zA-Z]+ [0-9]+$");
        using (SshShellChannel sshShellChannel = this._session.OpenShellChannel("ls -laRFH \"" + text4 + "\""))
        {
            sshShellChannel.DataReceived += outputCollector.OnDataReceived;
            sshShellChannel.ReadStderrOnRead = true;
            using StreamReader streamReader = new StreamReader(sshShellChannel.Output);
            while (!streamReader.EndOfStream)
            {
                string text = streamReader.ReadLine();
                if (text == null)
                {
                    break;
                }
                if (regex3.IsMatch(text) || text.Length == 0)
                {
                    continue;
                }
                if (text[text.Length - 1] == ':')
                {
                    string text2 = text.Substring(0, text.Length - 1);
                    if (this.Progress != null)
                    {
                        this.Progress(new CopyProgress(CopyStage.Counting, text2, value2, 0, value3, 0L));
                    }
                    continue;
                }
                int num = text.IndexOf(':');
                num = text.IndexOf(' ', num + 1);
                if (num == -1 && Log != null)
                {
                    Log(this, "Cannot parse 'ls' output: " + text);
                }
                string text3 = text.Substring(num + 1);
                num = text3.IndexOf("->");
                if (num != -1)
                {
                    text3.Substring(num + 2).Trim();
                    text3 = text3.Substring(0, num).Trim();
                }
                else
                {
                    text3 = text3.Trim();
                }
                if (text3.Length == 0)
                {
                    continue;
                }
                bool flag;
                switch (text3[text3.Length - 1])
                {
                    case '/':
                    case '=':
                    case '>':
                    case '@':
                    case '|':
                        flag = false;
                        break;
                    default:
                        flag = true;
                        break;
                }
                if (!flag)
                {
                    text3 = text3.Substring(0, text3.Length - 1);
                }
                if (!(text3 == ".") && !(text3 == "..") && !IsExcluded(text3, items))
                {
                    Match match = regex.Match(text);
                    if (!match.Success)
                    {
                        match = regex2.Match(text);
                    }
                    long result = 0L;
                    if ((!match.Success || !long.TryParse(match.Groups[1].ToString(), out result)) && Log != null)
                    {
                        Log(this, "Cannot determine file size from 'ls' output: " + text);
                    }
                    value3 += result;
                    if (flag)
                    {
                        value2++;
                    }
                }
            }
        }
        CheckErrors(outputCollector);
        return true;
    }

    private static bool IsExcluded(string text, Regex[] items)
    {
        if (items != null)
        {
            for (int num = 0; num < items.Length; num++)
            {
                if (items[num].IsMatch(text))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void CheckErrors(OutputCollector outputCollector)
    {
        if (Log != null)
        {
            using MemoryStream memoryStream = outputCollector.GetStream();
            if (memoryStream != null)
            {
                using StreamReader streamReader = new StreamReader(memoryStream);
                while (!streamReader.EndOfStream)
                {
                    string text = streamReader.ReadLine();
                    if (text == null)
                    {
                        break;
                    }
                    Log(this, text);
                }
            }
        }
        outputCollector.Clear();
    }

    public bool CountLocal(string text, out int value2, out long value3, PathFilter callback = null)
    {
        value2 = 0;
        value3 = 0L;
        return CountLocalRecursive(text, ref value2, ref value3, callback);
    }

    private bool CountLocalRecursive(string text, ref int value2, ref long value3, PathFilter callback)
    {
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress(CopyStage.Counting, text, value2, 0, value3, 0L));
        }
        List<string> list = new List<string>();
        IntPtr intPtr = LocalFileSystem.FindFirstFile(text + "\\*.*", out var findData);
        if (intPtr == (IntPtr)(-1))
        {
            return false;
        }
        try
        {
            do
            {
                if (!(findData.FileName == ".") && !(findData.FileName == ".."))
                {
                    if ((findData.Attributes & FileAttributes.Directory) != 0)
                    {
                        list.Add(findData.FileName);
                    }
                    else if (callback == null || callback(Path.Combine(text, findData.FileName)))
                    {
                        value2++;
                        value3 += (long)(((ulong)findData.FileSizeHigh << 32) | findData.FileSizeLow);
                    }
                }
            }
            while (LocalFileSystem.FindNextFile(intPtr, out findData));
        }
        finally
        {
            LocalFileSystem.FindClose(intPtr);
        }
        foreach (string item in list)
        {
            if (!CountLocalRecursive(Path.Combine(text, item), ref value2, ref value3, callback))
            {
                return false;
            }
        }
        return true;
    }

    private int RunCommand(string text)
    {
        using SshShellChannel sshShellChannel = ChannelFactory?.Invoke(text) ?? _session.OpenShellChannel(text, null, ExtendedDataMode.Merge);
        using (StreamReader streamReader = new StreamReader(sshShellChannel.Output))
        {
            while (streamReader.ReadLine() != null)
            {
            }
        }
        int result = sshShellChannel.ExitStatus;
        sshShellChannel.WaitClosed();
        return result;
    }

    public void Upload(string text, string text2, PathFilter callback = null, string[] items = null)
    {
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress(CopyStage.Counting, null, 0, 0, 0L, 0L));
        }
        if (!CountFirst || !CountLocal(text, out _fileCount, out _byteCount))
        {
            _fileCount = 0;
            _byteCount = 0L;
        }
        RunCommand($"mkdir -p \"{text2}\"");
        if (UseTar)
        {
            UploadViaTar(text2, text, callback, items);
            return;
        }
        long num = 0L;
        int num2 = 0;
        UploadFileByFile(text2, text, ref num2, ref num, callback);
    }

    private void UploadFileByFile(string text2, string text3, ref int value2, ref long value3, PathFilter callback)
    {
        List<string> list = new List<string>();
        IntPtr intPtr = LocalFileSystem.FindFirstFile(text3 + "\\*.*", out var findData);
        if (intPtr == (IntPtr)(-1))
        {
            return;
        }
        try
        {
            this._session.RunCommandChecked("mkdir \"" + text2 + "\"", requireSuccess: true);
            do
            {
                if (findData.FileName == "." || findData.FileName == "..")
                {
                    continue;
                }
                if ((findData.Attributes & FileAttributes.Directory) != 0)
                {
                    list.Add(findData.FileName);
                    continue;
                }
                string text = Path.Combine(text3, findData.FileName);
                if (callback != null && !callback(text))
                {
                    continue;
                }
                try
                {
                    CountLocalFiles(text2 + "/" + findData.FileName, ref value2, ref value3, text);
                }
                catch (Exception ex)
                {
                    if (Log != null)
                    {
                        Log(this, "Cannot upload " + text + ": " + ex.Message);
                    }
                }
            }
            while (LocalFileSystem.FindNextFile(intPtr, out findData));
        }
        finally
        {
            LocalFileSystem.FindClose(intPtr);
        }
        foreach (string item in list)
        {
            UploadFileByFile(text2 + "/" + item, Path.Combine(text3, item), ref value2, ref value3, callback);
        }
    }

    public void CountLocalFiles(string text, ref int value2, ref long value3, string text2)
    {
        if (TarOverwrite != null && this._session.RunCommandChecked("test -f '" + text + "'", requireSuccess: true) != null)
        {
            bool flag = false;
            TarOverwrite(text, ref flag);
            if (flag)
            {
                return;
            }
        }
        using FileStream fileStream = File.Open(text2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ScpChannelStream scpChannelStream = this._session.OpenUpload(text, fileStream.Length);
        CopyStream(fileStream, scpChannelStream, ref value2, ref value3, text2, -1L);
    }

    private void UploadViaTar(string text, string text2, PathFilter callback, string[] items)
    {
        RunCommand("mkdir -p \"" + text + "\"");
        string arg = TarCommands.GetExtractCommand(text, flag: false);
        byte[] buffer = new byte[1024];
        OutputCollector outputCollector = new OutputCollector();
        int num = 0;
        long num2 = 0L;
        using (SshShellChannel sshShellChannel = ChannelFactory?.Invoke(arg) ?? this._session.OpenShellChannel(arg))
        {
            sshShellChannel.DataReceived += outputCollector.OnDataReceived;
            sshShellChannel.ReadStderrOnRead = true;
            sshShellChannel.Output.ReadFully = true;
            WriteDirectoryTree(new TarWriter(items), sshShellChannel.Output, text2, "", ref num, ref num2, callback, buffer);
            sshShellChannel.Input.Write(buffer, 0, 1024);
            sshShellChannel.SendEof();
        }
        CheckErrors(outputCollector);
    }

    private void WriteDirectoryTree(TarWriter tarWriter, Stream stream, string text4, string text5, ref int value2, ref long value3, PathFilter callback, byte[] buffer)
    {
        List<DirectoryEntry> list = new List<DirectoryEntry>();
        IntPtr intPtr = LocalFileSystem.FindFirstFile(text4 + "\\*.*", out var findData);
        if (intPtr == (IntPtr)(-1))
        {
            return;
        }
        int count;
        try
        {
            do
            {
                if (findData.FileName == "." || findData.FileName == "..")
                {
                    continue;
                }
                if ((findData.Attributes & FileAttributes.Directory) != 0)
                {
                    list.Add(new DirectoryEntry
                    {
                        Name = findData.FileName,
                        Time = DateTime.FromFileTime(LocalFileSystem.MakeLong(findData.LastWriteTime.dwHighDateTime, findData.LastWriteTime.dwLowDateTime))
                    });
                    continue;
                }
                string text = Path.Combine(text4, findData.FileName);
                if (callback != null && !callback(text))
                {
                    continue;
                }
                try
                {
                    using FileStream fileStream = File.Open(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    string text2 = text5;
                    if (text2 != "")
                    {
                        text2 += "/";
                    }
                    text2 += findData.FileName;
                    DateTime dateTime = DateTime.FromFileTimeUtc(LocalFileSystem.MakeLong(findData.LastWriteTime.dwHighDateTime, findData.LastWriteTime.dwLowDateTime));
                    long length = fileStream.Length;
                    byte[] array = tarWriter.CreateHeader(text2, flag: false, length, dateTime, out count);
                    stream.Write(array, 0, array.Length);
                    CopyStream(fileStream, stream, ref value2, ref value3, text, length);
                    stream.Write(buffer, 0, count);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex2)
                {
                    if (this.Log != null)
                    {
                        this.Log(this, "Cannot upload " + text + ": " + ex2.Message);
                    }
                }
            }
            while (LocalFileSystem.FindNextFile(intPtr, out findData));
        }
        finally
        {
            LocalFileSystem.FindClose(intPtr);
        }
        foreach (DirectoryEntry item in list)
        {
            string text3 = text5;
            if (text3 != "")
            {
                text3 += "/";
            }
            text3 += item.Name;
            stream.Write(tarWriter.CreateHeader(text3, flag: true, 0L, item.Time, out count), 0, 512);
            WriteDirectoryTree(tarWriter, stream, Path.Combine(text4, item.Name), text3, ref value2, ref value3, callback, buffer);
        }
    }

    public static long CopyBetweenHosts(SshSession sshSession, string text5, SshSession sshSession2, string text6, Action<object, string> callback)
    {
        string text = TarCommands.GetCreateCommand(text5, flag: false);
        string text2 = TarCommands.GetExtractCommand(text6, flag: false);
        string text3 = $"mkdir -p \"{text6}\"";
        long num = 0L;
        sshSession2.RunCommandChecked(text3, requireSuccess: false);
        bool flag = false;
        OutputCollector outputCollector = new OutputCollector();
        using (SshShellChannel sshShellChannel = sshSession.OpenShellChannel(text))
        {
            sshShellChannel.DataReceived += outputCollector.OnDataReceived;
            sshShellChannel.ReadStderrOnRead = true;
            sshShellChannel.Output.ReadFully = true;
            using (SshShellChannel sshShellChannel2 = sshSession2.OpenShellChannel(text2))
            {
                sshShellChannel2.DataReceived += outputCollector.OnDataReceived;
                sshShellChannel2.ReadStderrOnRead = true;
                sshShellChannel2.Output.ReadFully = true;
                byte[] array = new byte[262144];
                while (true)
                {
                    int num2 = sshShellChannel.Output.Read(array, 0, array.Length);
                    if (num2 == 0)
                    {
                        break;
                    }
                    sshShellChannel2.Input.Write(array, 0, num2);
                    num += num2;
                }
                sshShellChannel2.SendEof();
            }
            flag |= sshShellChannel.ExitStatus != 0;
        }
        if (callback != null)
        {
            using MemoryStream memoryStream = outputCollector.GetStream();
            if (memoryStream != null)
            {
                using StreamReader streamReader = new StreamReader(memoryStream);
                while (!streamReader.EndOfStream)
                {
                    string text4 = streamReader.ReadLine();
                    if (text4 == null)
                    {
                        break;
                    }
                    callback(null, text4);
                }
            }
        }
        if (flag)
        {
            throw new Exception("Failed to transfer " + text5 + " to " + text6);
        }
        return num;
    }
}
