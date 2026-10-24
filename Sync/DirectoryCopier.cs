using SmarTTY.Ssh;
using SmarTTY.Ssh.QuickSync;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace SmarTTY.Sync;

public class DirectoryCopier
{
    public static class TarCommands
    {
        public static string GetExtractCommand(string dir, bool preserveTimestamps)
        {
            if (preserveTimestamps)
            {
                return $"cd {SmarTTY.IO.RemotePathUtils.ShellQuote(dir)} && tar -xmf -";
            }
            return $"cd {SmarTTY.IO.RemotePathUtils.ShellQuote(dir)} && tar -xf -";
        }

        public static string GetCreateCommand(string dir, bool followSymlinks, string filePattern = "*", string[] excludeItems = null)
        {
            string flagStr = (followSymlinks ? "h" : "");
            StringBuilder stringBuilder = new StringBuilder();
            if (excludeItems != null)
            {
                foreach (string arg in excludeItems)
                {
                    stringBuilder.AppendFormat(" --exclude={0}", SmarTTY.IO.RemotePathUtils.ShellQuote(arg));
                }
            }
            return $"cd {SmarTTY.IO.RemotePathUtils.ShellQuote(dir)} && tar{stringBuilder} -c{flagStr}f - {filePattern}";
        }

        public static string GetCreateFromListCommand(string dir, bool followSymlinks, string listFileName, string[] excludeItems = null)
        {
            string flagStr = (followSymlinks ? "h" : "");
            StringBuilder stringBuilder = new StringBuilder();
            if (excludeItems != null)
            {
                foreach (string arg in excludeItems)
                {
                    stringBuilder.AppendFormat(" --exclude={0}", SmarTTY.IO.RemotePathUtils.ShellQuote(arg));
                }
            }
            return $"cd {SmarTTY.IO.RemotePathUtils.ShellQuote(dir)} && tar{stringBuilder} -c{flagStr}T {SmarTTY.IO.RemotePathUtils.ShellQuote(listFileName)} -f -";
        }
    }

    private class OutputCollector
    {
        private byte[] _data = new byte[0];

        public void OnDataReceived(object sender, byte[] buffer, int offset, int count, int dataType)
        {
            int oldLen = this._data.Length;
            Array.Resize(ref this._data, oldLen + count);
            Array.Copy(buffer, offset, this._data, oldLen, count);
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

        /// <summary>The non-empty lines collected so far.</summary>
        internal List<string> GetLines()
        {
            List<string> lines = new List<string>();
            if (_data.Length != 0)
            {
                foreach (string line in Encoding.UTF8.GetString(_data).Split('\n'))
                {
                    if (line.Trim().Length > 0)
                    {
                        lines.Add(line.Trim());
                    }
                }
            }
            return lines;
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

    public struct CopyProgress(CopyStage copyStage, string path, int filesDone, int filesTotal, long bytesDone, long bytesTotal)
    {
        public readonly CopyStage Stage = copyStage;

        public readonly string Path = path;

        public readonly int FilesDone = filesDone;

        public readonly int FilesTotal = filesTotal;

        public readonly long BytesDone = bytesDone;

        public readonly long BytesTotal = bytesTotal;
    }

    public delegate void ProgressHandler(CopyProgress copyProgress);

    public delegate bool PathFilter(string path);

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

    public DirectoryCopier(SshSession sshSession, string tempPath = "/tmp")
    {
        _session = sshSession;
        _tempPath = tempPath;
    }

    public void Download(string remotePath, string localPath, string statsFilePath = null, string[] excludeItems = null, bool preserveTimestamps = false)
    {
        Directory.CreateDirectory(localPath);
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress(CopyStage.Counting, null, 0, 0, 0L, 0L));
        }
        Regex[] excludeRegexes = null;
        if (excludeItems != null)
        {
            excludeRegexes = new Regex[excludeItems.Length];
            for (int i = 0; i < excludeItems.Length; i++)
            {
                excludeRegexes[i] = WildcardParser.WildcardToRegex(excludeItems[i]);
            }
        }
        if (!CountFirst || !CountRemote(remotePath, out _fileCount, out _byteCount, excludeRegexes))
        {
            _fileCount = 0;
            _byteCount = 0L;
        }
        if (UseTar)
        {
            DownloadViaTar(remotePath, localPath, statsFilePath, excludeItems, preserveTimestamps);
            return;
        }
        long totalBytes = 0L;
        int totalFiles = 0;
        DownloadFileByFile(remotePath, localPath, ref totalFiles, ref totalBytes, excludeRegexes);
        if (string.IsNullOrEmpty(statsFilePath))
        {
            return;
        }
        XmlSerializer xmlSerializer = new XmlSerializer(typeof(DirectoryStatistics));
        using FileStream stream = File.Create(statsFilePath);
        xmlSerializer.Serialize(stream, new DirectoryStatistics
        {
            TotalBytes = totalBytes,
            TotalFiles = totalFiles
        });
    }

    public void DownloadViaTar(string remotePath, string localPath, string statsFilePath, string[] excludeItems, bool preserveTimestamps = false, string filePattern = "*")
    {
        OutputCollector outputCollector = new OutputCollector();
        string command = TarCommands.GetCreateCommand(remotePath, FollowSymlinks, filePattern, excludeItems);
        DirectoryStatistics directoryStatistics = ((statsFilePath == null) ? null : new DirectoryStatistics());
        List<string> symlinkList;
        using (SshShellChannel sshShellChannel = this._session.OpenShellChannel(command))
        {
            Directory.CreateDirectory(localPath);
            sshShellChannel.DataReceived += outputCollector.OnDataReceived;
            sshShellChannel.ReadStderrOnRead = true;
            sshShellChannel.Output.ReadFully = true;
            TarExtractor tarExtractor = new TarExtractor(sshShellChannel.Output, localPath)
            {
                PreserveTimestamps = true
            };
            if (TarOverwrite != null)
            {
                tarExtractor.FileOverwriting += TarOverwrite;
            }
            tarExtractor.Log += this.Log;
            tarExtractor.Progress = OnTarProgress;
            tarExtractor.ExtractAll(ignoreErrors: true, stripFirstDirectory: false);
            tarExtractor.CreateSymlinks(out symlinkList, remotePath);
            tarExtractor.AddStatisticsTo(directoryStatistics);
            CheckErrors(outputCollector);
            ThrowIfTarSourceLost(sshShellChannel, remotePath);
        }
        if (symlinkList != null && symlinkList.Count != 0 && UseTempFileList)
        {
            string tempListFile = _tempPath + "/" + $"ssh{DateTime.Now.ToFileTimeUtc()}-{Process.GetCurrentProcess().Id}-{Environment.MachineName}";
            for (int pass = 0; pass < 1; pass++)
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    using StreamWriter streamWriter = new StreamWriter(memoryStream);
                    streamWriter.NewLine = "\n";
                    foreach (string linkItem in symlinkList)
                    {
                        streamWriter.WriteLine(linkItem);
                    }
                    streamWriter.Flush();
                    using ScpChannelStream scpChannelStream = this._session.OpenUpload(tempListFile, memoryStream.Length);
                    scpChannelStream.Write(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
                }
                command = TarCommands.GetCreateFromListCommand(remotePath, followSymlinks: true, tempListFile, excludeItems);
                using (SshShellChannel shellChannel = this._session.OpenShellChannel(command))
                {
                    shellChannel.DataReceived += outputCollector.OnDataReceived;
                    shellChannel.ReadStderrOnRead = true;
                    shellChannel.Output.ReadFully = true;
                    TarExtractor tarExtractor = new TarExtractor(shellChannel.Output, localPath);
                    tarExtractor.PreserveTimestamps = preserveTimestamps;
                    tarExtractor.Progress = OnTarListProgress;
                    tarExtractor.ExtractAll(ignoreErrors: true, stripFirstDirectory: false);
                    tarExtractor.CreateSymlinks(out symlinkList, remotePath);
                    tarExtractor.AddStatisticsTo(directoryStatistics);
                    CheckErrors(outputCollector);
                    ThrowIfTarSourceLost(shellChannel, remotePath);
                }
                if (symlinkList.Count == 0)
                {
                    break;
                }
            }
            this._session.RunCommandChecked("rm " + SmarTTY.IO.RemotePathUtils.ShellQuote(tempListFile), requireSuccess: false);
        }
        if (!string.IsNullOrEmpty(statsFilePath) && directoryStatistics != null)
        {
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(DirectoryStatistics));
            using FileStream stream = File.Create(statsFilePath);
            xmlSerializer.Serialize(stream, directoryStatistics);
        }
    }

    private void DownloadFileByFile(string remoteDir, string localDir, ref int filesDone, ref long bytesDone, Regex[] excludeRegexes)
    {
        List<string> fileList = new List<string>();
        List<string> dirList = new List<string>();
        List<string> rawLines = this._session.RunCommandChecked("ls -1ALF " + SmarTTY.IO.RemotePathUtils.ShellQuote(remoteDir), requireSuccess: true);
        if (rawLines == null)
        {
            if (Log != null)
            {
                Log(this, "Cannot list contents of " + remoteDir);
            }
            return;
        }
        foreach (string line in rawLines)
        {
            if (!string.IsNullOrEmpty(line))
            {
                if (line[line.Length - 1] == '/')
                {
                    dirList.Add(line.Substring(0, line.Length - 1));
                }
                else if (line[line.Length - 1] == '*')
                {
                    fileList.Add(line.Substring(0, line.Length - 1));
                }
                else if ("@/=>|".IndexOf(line[line.Length - 1]) == -1)
                {
                    fileList.Add(line);
                }
            }
        }
        Directory.CreateDirectory(localDir);
        foreach (string fileName in fileList)
        {
            string localFilePath = Path.Combine(localDir, fileName);
            if (IsExcluded(fileName, excludeRegexes))
            {
                continue;
            }
            try
            {
                this.CountFiles(remoteDir + "/" + fileName, ref filesDone, ref bytesDone, localFilePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (Log != null)
                {
                    Log(this, "Cannot retrieve contents of " + localFilePath + ": " + exception.Message);
                }
            }
        }
        foreach (string subDir in dirList)
        {
            DownloadFileByFile(remoteDir + "/" + subDir, localDir + "\\" + subDir, ref filesDone, ref bytesDone, excludeRegexes);
        }
    }

    public void CountFiles(string remotePath, ref int filesDone, ref long bytesDone, string localPath)
    {
        if (TarOverwrite != null && File.Exists(localPath))
        {
            bool cancel = false;
            TarOverwrite(localPath, ref cancel);
            if (cancel)
            {
                return;
            }
        }
        using ScpDownloadStream scpDownloadStream = this._session.OpenDownload(remotePath);
        using FileStream fileStream = File.Create(localPath);
        scpDownloadStream.AbortOnShortRead = true;
        CopyStream(scpDownloadStream, fileStream, ref filesDone, ref bytesDone, localPath, -1L);
    }

    private void CopyStream(Stream sourceStream, Stream destinationStream, ref int filesDone, ref long bytesDone, string currentPath, long length = -1L)
    {
        if (_buffer == null)
        {
            _buffer = new byte[1048576];
        }
        byte[] buffer = _buffer;
        long totalRead = 0L;
        if (length < 0)
        {
            length = sourceStream.Length;
        }
        while (totalRead < length)
        {
            long bytesToRead = Math.Min(buffer.LongLength, length - totalRead);
            int bytesRead = sourceStream.Read(buffer, 0, (int)bytesToRead);
            if (bytesRead <= 0)
            {
                break;
            }
            destinationStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
            bytesDone += bytesRead;
            if (this.Progress != null)
            {
                this.Progress(new CopyProgress(CopyStage.CopyingFile, currentPath, filesDone, _fileCount, bytesDone, _byteCount));
            }
        }
        filesDone++;
    }

    private void OnTarProgress(bool isHeader, string entryName, long entryOffset, long totalBytesDone, int filesDone, long fileBytesDone)
    {
        if (!isHeader)
        {
            _bytesDone = Math.Max(_bytesDone, totalBytesDone + fileBytesDone);
        }
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress((!isHeader) ? CopyStage.CopyingFile : CopyStage.CopyingTarEntry, entryName, filesDone, _fileCount, totalBytesDone + fileBytesDone, _byteCount));
        }
    }

    private void OnTarListProgress(bool isHeader, string entryName, long entryOffset, long totalBytesDone, int filesDone, long fileBytesDone)
    {
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress(CopyStage.StreamingTar, entryName, 0, 0, _bytesDone + totalBytesDone + fileBytesDone, 0L));
        }
    }

    public bool CountRemote(string remotePath, out int filesDone, out long totalBytesDone, Regex[] excludeRegexes = null)
    {
        OutputCollector outputCollector = new OutputCollector();
        filesDone = 0;
        totalBytesDone = 0L;
        Regex lsFullRegex = new Regex("^[^ \t]+[ \t]+[^ \t]+[ \t]+[^ \t]+[ \t]+[^ \t]+[ \t]+([0-9]+)[ \t]+");
        Regex lsShortRegex = new Regex("^[^ \t]+[ \t]+[^ \t]+[ \t]+[^ \t]+[ \t]+([0-9]+)[ \t]+");
        Regex lsHeaderRegex = new Regex("^[a-zA-Z]+ [0-9]+$");
        using (SshShellChannel sshShellChannel = this._session.OpenShellChannel("ls -laRFH " + SmarTTY.IO.RemotePathUtils.ShellQuote(remotePath)))
        {
            sshShellChannel.DataReceived += outputCollector.OnDataReceived;
            sshShellChannel.ReadStderrOnRead = true;
            using StreamReader streamReader = new StreamReader(sshShellChannel.Output);
            while (!streamReader.EndOfStream)
            {
                string line = streamReader.ReadLine();
                if (line == null)
                {
                    break;
                }
                if (lsHeaderRegex.IsMatch(line) || line.Length == 0)
                {
                    continue;
                }
                if (line[line.Length - 1] == ':')
                {
                    string dirName = line.Substring(0, line.Length - 1);
                    if (this.Progress != null)
                    {
                        this.Progress(new CopyProgress(CopyStage.Counting, dirName, filesDone, 0, totalBytesDone, 0L));
                    }
                    continue;
                }
                int colonIdx = line.IndexOf(':');
                colonIdx = line.IndexOf(' ', colonIdx + 1);
                if (colonIdx == -1 && Log != null)
                {
                    Log(this, "Cannot parse 'ls' output: " + line);
                }
                string entryName = line.Substring(colonIdx + 1);
                colonIdx = entryName.IndexOf("->");
                if (colonIdx != -1)
                {
                    entryName.Substring(colonIdx + 2).Trim();
                    entryName = entryName.Substring(0, colonIdx).Trim();
                }
                else
                {
                    entryName = entryName.Trim();
                }
                if (entryName.Length == 0)
                {
                    continue;
                }
                bool isRegularFile;
                switch (entryName[entryName.Length - 1])
                {
                    case '/':
                    case '=':
                    case '>':
                    case '@':
                    case '|':
                        isRegularFile = false;
                        break;
                    default:
                        isRegularFile = true;
                        break;
                }
                if (!isRegularFile)
                {
                    entryName = entryName.Substring(0, entryName.Length - 1);
                }
                if (!(entryName == ".") && !(entryName == "..") && !IsExcluded(entryName, excludeRegexes))
                {
                    Match match = lsFullRegex.Match(line);
                    if (!match.Success)
                    {
                        match = lsShortRegex.Match(line);
                    }
                    long fileSize = 0L;
                    if ((!match.Success || !long.TryParse(match.Groups[1].ToString(), out fileSize)) && Log != null)
                    {
                        Log(this, "Cannot determine file size from 'ls' output: " + line);
                    }
                    totalBytesDone += fileSize;
                    if (isRegularFile)
                    {
                        filesDone++;
                    }
                }
            }
        }
        CheckErrors(outputCollector);
        return true;
    }

    private static bool IsExcluded(string name, Regex[] excludeRegexes)
    {
        if (excludeRegexes != null)
        {
            for (int i = 0; i < excludeRegexes.Length; i++)
            {
                if (excludeRegexes[i].IsMatch(name))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// A connection lost between two tar records reads as a normal end of the archive (TarExtractor takes 0 bytes at a
    /// record boundary for the end), so the channel is asked how it ended. A non-zero exit status alone is only
    /// logged: tar also uses it for files it skipped, which the download tolerates.
    /// </summary>
    private void ThrowIfTarSourceLost(SshShellChannel channel, string remotePath)
    {
        channel.WaitEof();
        if (channel.ClosedByConnectionLoss)
        {
            throw new IOException("The connection to the server was lost while downloading " + remotePath + ". The files may be incomplete.");
        }
        int status = channel.ExitStatus;
        if (status != 0)
        {
            Log?.Invoke(this, "tar finished with exit status " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " while reading " + remotePath + ": some files may be missing.");
        }
    }

    /// <summary>A tar message about file attributes that could not be set (owner, time stamp, mode), not about missing data.</summary>
    private static bool IsHarmlessTarMessage(string message)
    {
        string[] harmless = { "change ownership", "chown", "utime", "time stamp", "timestamp", "change mode", "chmod", "set permissions", "implausibly old" };
        return harmless.Any((string part) => message.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
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
                    string line = streamReader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }
                    Log(this, line);
                }
            }
        }
        outputCollector.Clear();
    }

    public bool CountLocal(string localPath, out int filesDone, out long totalBytesDone, PathFilter filter = null)
    {
        filesDone = 0;
        totalBytesDone = 0L;
        return CountLocalRecursive(localPath, ref filesDone, ref totalBytesDone, filter);
    }

    private bool CountLocalRecursive(string localPath, ref int filesDone, ref long totalBytesDone, PathFilter filter)
    {
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress(CopyStage.Counting, localPath, filesDone, 0, totalBytesDone, 0L));
        }
        List<string> subDirs = new List<string>();
        IntPtr findHandle = LocalFileSystem.FindFirstFile(localPath + "\\*.*", out var findData);
        if (findHandle == (IntPtr)(-1))
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
                        subDirs.Add(findData.FileName);
                    }
                    else if (filter == null || filter(Path.Combine(localPath, findData.FileName)))
                    {
                        filesDone++;
                        totalBytesDone += (long)(((ulong)findData.FileSizeHigh << 32) | findData.FileSizeLow);
                    }
                }
            }
            while (LocalFileSystem.FindNextFile(findHandle, out findData));
        }
        finally
        {
            LocalFileSystem.FindClose(findHandle);
        }
        foreach (string subDir in subDirs)
        {
            if (!CountLocalRecursive(Path.Combine(localPath, subDir), ref filesDone, ref totalBytesDone, filter))
            {
                return false;
            }
        }
        return true;
    }

    private int RunCommand(string command)
    {
        using SshShellChannel sshShellChannel = ChannelFactory?.Invoke(command) ?? _session.OpenShellChannel(command, null, ExtendedDataMode.Merge);
        using (StreamReader streamReader = new StreamReader(sshShellChannel.Output))
        {
            while (streamReader.ReadLine() != null)
            {
            }
        }
        int exitStatus = sshShellChannel.ExitStatus;
        sshShellChannel.WaitClosed();
        return exitStatus;
    }

    public void Upload(string localPath, string remotePath, PathFilter filter = null, string[] excludeItems = null)
    {
        if (this.Progress != null)
        {
            this.Progress(new CopyProgress(CopyStage.Counting, null, 0, 0, 0L, 0L));
        }
        if (!CountFirst || !CountLocal(localPath, out _fileCount, out _byteCount))
        {
            _fileCount = 0;
            _byteCount = 0L;
        }
        RunCommand("mkdir -p " + SmarTTY.IO.RemotePathUtils.ShellQuote(remotePath));
        if (UseTar)
        {
            UploadViaTar(remotePath, localPath, filter, excludeItems);
            return;
        }
        long totalBytes = 0L;
        int filesDone = 0;
        UploadFileByFile(remotePath, localPath, ref filesDone, ref totalBytes, filter);
    }

    private void UploadFileByFile(string remotePath, string localPath, ref int filesDone, ref long bytesDone, PathFilter filter)
    {
        List<string> subDirs = new List<string>();
        IntPtr findHandle = LocalFileSystem.FindFirstFile(localPath + "\\*.*", out var findData);
        if (findHandle == (IntPtr)(-1))
        {
            return;
        }
        try
        {
            this._session.RunCommandChecked("mkdir " + SmarTTY.IO.RemotePathUtils.ShellQuote(remotePath), requireSuccess: true);
            do
            {
                if (findData.FileName == "." || findData.FileName == "..")
                {
                    continue;
                }
                if ((findData.Attributes & FileAttributes.Directory) != 0)
                {
                    subDirs.Add(findData.FileName);
                    continue;
                }
                string localFilePath = Path.Combine(localPath, findData.FileName);
                if (filter != null && !filter(localFilePath))
                {
                    continue;
                }
                try
                {
                    CountLocalFiles(remotePath + "/" + findData.FileName, ref filesDone, ref bytesDone, localFilePath);
                }
                catch (Exception exception)
                {
                    if (Log != null)
                    {
                        Log(this, "Cannot upload " + localFilePath + ": " + exception.Message);
                    }
                }
            }
            while (LocalFileSystem.FindNextFile(findHandle, out findData));
        }
        finally
        {
            LocalFileSystem.FindClose(findHandle);
        }
        foreach (string subDir in subDirs)
        {
            UploadFileByFile(remotePath + "/" + subDir, Path.Combine(localPath, subDir), ref filesDone, ref bytesDone, filter);
        }
    }

    public void CountLocalFiles(string remotePath, ref int filesDone, ref long bytesDone, string localPath)
    {
        if (TarOverwrite != null && this._session.RunCommandChecked("test -f " + SmarTTY.IO.RemotePathUtils.ShellQuote(remotePath), requireSuccess: true) != null)
        {
            bool cancel = false;
            TarOverwrite(remotePath, ref cancel);
            if (cancel)
            {
                return;
            }
        }
        using FileStream fileStream = File.Open(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ScpChannelStream scpChannelStream = this._session.OpenUpload(remotePath, fileStream.Length);
        CopyStream(fileStream, scpChannelStream, ref filesDone, ref bytesDone, localPath, -1L);
    }

    private void UploadViaTar(string remotePath, string localPath, PathFilter filter, string[] excludeItems)
    {
        RunCommand("mkdir -p " + SmarTTY.IO.RemotePathUtils.ShellQuote(remotePath));
        string extractCommand = TarCommands.GetExtractCommand(remotePath, preserveTimestamps: false);
        byte[] paddingBuffer = new byte[1024];
        OutputCollector outputCollector = new OutputCollector();
        int filesDone = 0;
        long totalBytesDone = 0L;
        using (SshShellChannel sshShellChannel = ChannelFactory?.Invoke(extractCommand) ?? this._session.OpenShellChannel(extractCommand))
        {
            sshShellChannel.DataReceived += outputCollector.OnDataReceived;
            sshShellChannel.ReadStderrOnRead = true;
            sshShellChannel.Output.ReadFully = true;
            WriteDirectoryTree(new TarWriter(excludeItems), sshShellChannel.Output, localPath, "", ref filesDone, ref totalBytesDone, filter, paddingBuffer);
            sshShellChannel.Input.Write(paddingBuffer, 0, 1024);
            sshShellChannel.SendEof();
            // Let the extracting tar finish and look at how it ended: on a channel closed by a lost connection
            // SendEof does nothing and Dispose swallows everything, so without this the upload would end as a success
            // with data that never reached the device (or that tar could not unpack).
            sshShellChannel.WaitEof();
            List<string> tarMessages = outputCollector.GetLines();
            CheckErrors(outputCollector);
            if (sshShellChannel.ClosedByConnectionLoss)
            {
                throw new IOException("The connection to the server was lost while uploading to " + remotePath + ". The files may be incomplete.");
            }
            int tarStatus = sshShellChannel.ExitStatus;
            if (tarStatus != 0 && tarMessages.Count > 0 && tarMessages.All(IsHarmlessTarMessage))
            {
                // tar reports what it could not set on the extracted files (owner, time stamp, mode) with a non-zero
                // status too, but the data is there: the messages are in the log (CheckErrors), the upload succeeded.
                Log?.Invoke(this, "tar finished with exit status " + tarStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) + " after warnings only; the files were unpacked.");
            }
            else if (tarStatus != 0)
            {
                throw new IOException("The device could not unpack the uploaded files into " + remotePath + " (tar exit status " + tarStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) + "). The files may be incomplete.");
            }
        }
    }

    private void WriteDirectoryTree(TarWriter tarWriter, Stream destinationStream, string localDirPath, string relativePath, ref int filesDone, ref long bytesDone, PathFilter filter, byte[] paddingBuffer)
    {
        List<DirectoryEntry> subDirs = new List<DirectoryEntry>();
        IntPtr findHandle = LocalFileSystem.FindFirstFile(localDirPath + "\\*.*", out var findData);
        if (findHandle == (IntPtr)(-1))
        {
            return;
        }
        int paddingBytes;
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
                    subDirs.Add(new DirectoryEntry
                    {
                        Name = findData.FileName,
                        Time = DateTime.FromFileTime(LocalFileSystem.MakeLong(findData.LastWriteTime.dwHighDateTime, findData.LastWriteTime.dwLowDateTime))
                    });
                    continue;
                }
                string localFilePath = Path.Combine(localDirPath, findData.FileName);
                if (filter != null && !filter(localFilePath))
                {
                    continue;
                }
                bool headerWritten = false;
                try
                {
                    using FileStream fileStream = File.Open(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    string tarEntryPath = relativePath;
                    if (tarEntryPath != "")
                    {
                        tarEntryPath += "/";
                    }
                    tarEntryPath += findData.FileName;
                    DateTime modifiedTime = DateTime.FromFileTimeUtc(LocalFileSystem.MakeLong(findData.LastWriteTime.dwHighDateTime, findData.LastWriteTime.dwLowDateTime));
                    long length = fileStream.Length;
                    byte[] headerBytes = tarWriter.CreateHeader(tarEntryPath, isDirectory: false, length, modifiedTime, out paddingBytes);
                    destinationStream.Write(headerBytes, 0, headerBytes.Length);
                    headerWritten = true;
                    CopyStream(fileStream, destinationStream, ref filesDone, ref bytesDone, localFilePath, length);
                    destinationStream.Write(paddingBuffer, 0, paddingBytes);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (!headerWritten)
                {
                    // Only a file that failed before its tar header was written can be skipped; after that the
                    // stream holds a half entry and anything written next would be read as garbage.
                    if (this.Log != null)
                    {
                        this.Log(this, "Cannot upload " + localFilePath + ": " + exception.Message);
                    }
                }
            }
            while (LocalFileSystem.FindNextFile(findHandle, out findData));
        }
        finally
        {
            LocalFileSystem.FindClose(findHandle);
        }
        foreach (DirectoryEntry dirEntry in subDirs)
        {
            string childRelativePath = relativePath;
            if (childRelativePath != "")
            {
                childRelativePath += "/";
            }
            childRelativePath += dirEntry.Name;
            destinationStream.Write(tarWriter.CreateHeader(childRelativePath, isDirectory: true, 0L, dirEntry.Time, out paddingBytes), 0, 512);
            WriteDirectoryTree(tarWriter, destinationStream, Path.Combine(localDirPath, dirEntry.Name), childRelativePath, ref filesDone, ref bytesDone, filter, paddingBuffer);
        }
    }

    public static long CopyBetweenHosts(SshSession sourceSession, string sourcePath, SshSession destinationSession, string destinationPath, Action<object, string> logCallback)
    {
        string createCommand = TarCommands.GetCreateCommand(sourcePath, followSymlinks: false);
        string extractCommand = TarCommands.GetExtractCommand(destinationPath, preserveTimestamps: false);
        string mkdirCommand = "mkdir -p " + SmarTTY.IO.RemotePathUtils.ShellQuote(destinationPath);
        long totalTransferred = 0L;
        destinationSession.RunCommandChecked(mkdirCommand, requireSuccess: false);
        bool hasError = false;
        OutputCollector outputCollector = new OutputCollector();
        using (SshShellChannel sourceChannel = sourceSession.OpenShellChannel(createCommand))
        {
            sourceChannel.DataReceived += outputCollector.OnDataReceived;
            sourceChannel.ReadStderrOnRead = true;
            sourceChannel.Output.ReadFully = true;
            using (SshShellChannel destChannel = destinationSession.OpenShellChannel(extractCommand))
            {
                destChannel.DataReceived += outputCollector.OnDataReceived;
                destChannel.ReadStderrOnRead = true;
                destChannel.Output.ReadFully = true;
                byte[] buffer = new byte[262144];
                while (true)
                {
                    int bytesRead = sourceChannel.Output.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    destChannel.Input.Write(buffer, 0, bytesRead);
                    totalTransferred += bytesRead;
                }
                destChannel.SendEof();
                // Let the extracting tar finish and look at how it ended; dropping the channel right after the EOF
                // would also drop its result (a lost connection, a failed extraction).
                destChannel.WaitEof();
                hasError |= destChannel.ClosedByConnectionLoss || destChannel.ExitStatus != 0;
            }
            // A source lost with the connection reads as the end of the tar stream (the data is cut short), but its
            // exit status is not 0 then.
            hasError |= sourceChannel.ClosedByConnectionLoss || sourceChannel.ExitStatus != 0;
        }
        if (logCallback != null)
        {
            using MemoryStream memoryStream = outputCollector.GetStream();
            if (memoryStream != null)
            {
                using StreamReader streamReader = new StreamReader(memoryStream);
                while (!streamReader.EndOfStream)
                {
                    string logLine = streamReader.ReadLine();
                    if (logLine == null)
                    {
                        break;
                    }
                    logCallback(null, logLine);
                }
            }
        }
        if (hasError)
        {
            throw new Exception("Failed to transfer " + sourcePath + " to " + destinationPath);
        }
        return totalTransferred;
    }
}
