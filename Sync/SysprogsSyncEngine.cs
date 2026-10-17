using SmarTTY.Ssh;
using SmarTTY.Ssh.QuickSync;
using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace SmarTTY.Sync;

internal class SysprogsSyncEngine : IDisposable, ISyncEngine
{
    public class SysprogsSyncException : Exception
    {
        public SysprogsSyncException(string message)
            : base(message)
        {
        }
    }

    private enum SessionMode
    {
        Download,
        Upload,
        Sync
    }

    private enum RemoteCommand
    {
        None,
        CreateAndEnterDirectory,
        EnterDirectory,
        GoToParent,
        SendFile,
        DeleteFile,
        DeleteEmptyDirectory,
        DeleteDirectoryRecursively,
        CreateSymlink,
        PrintMessage,
        End
    }

    private enum RemoteReply
    {
        Unknown,
        ActionFailed,
        ActionDone
    }

    [Flags]
    private enum SessionFlags
    {
        None = 0,
        OneFileSystem = 1,
        UploadMode = 2
    }

    private class SysprogsSyncFileSource : IRemoteFileSource, IDisposable
    {
        private class SysprogsSyncFileStream : IRemoteFileStream, IDisposable
        {
            private SysprogsSyncFileSource _source;

            private bool _finished;

            public int ErrorCode { get; }

            public long ModifiedTime { get; }

            public SysprogsSyncFileStream(SysprogsSyncFileSource sysprogsSyncFileSource, LocalFileSystem.FileStamp fileStamp, int errorCode)
            {
                this._source = sysprogsSyncFileSource;
                ModifiedTime = fileStamp.LastWriteFileTime;
                this.ErrorCode = errorCode;
                if (errorCode != 0)
                {
                    _finished = true;
                }
            }

            public void Dispose()
            {
                if (!_finished)
                {
                    throw new Exception("Remote file stream deleted before reaching EOF. The remaining stream will be unreadable.");
                }
            }

            public int ReadChunk(ref byte[] buffer)
            {
                if (_finished)
                {
                    return 0;
                }
                int chunkSize = _source._reader.ReadInt32();
                if (buffer == null || buffer.Length < chunkSize)
                {
                    buffer = new byte[chunkSize];
                }
                if (chunkSize == 0)
                {
                    _finished = true;
                    _source._transferActive = false;
                }
                _source._reader.ReadBytes(buffer, chunkSize);
                return chunkSize;
            }
        }

        private SyncStreamReader _reader;

        private byte[] _headerBuffer = new byte[12];

        private bool _transferActive;

        public SysprogsSyncFileSource(SshShellChannel sshShellChannel)
        {
            this._reader = new SyncStreamReader(sshShellChannel, 1048576);
        }

        public void Dispose()
        {
            int marker = _reader.ReadInt32();
            if (marker != 1162760004)
            {
                throw new Exception("Expected end of remote SysprogsSync stream, got " + marker);
            }
        }

        public IRemoteFileStream OpenFile(int fileId)
        {
            if (_transferActive)
            {
                throw new Exception("The previous file transfer is still in progress");
            }
            this._reader.ReadBytes(_headerBuffer, 12);
            int receivedFileId = BitConverter.ToInt32(_headerBuffer, 0);
            if (receivedFileId != fileId)
            {
                throw new Exception($"Remote SysprogsSync sent unexpected fileID: {receivedFileId}, expected {fileId}");
            }
            int errorCode = BitConverter.ToInt32(_headerBuffer, 4);
            LocalFileSystem.FileStamp fileStamp = new LocalFileSystem.FileStamp(0L, BitConverter.ToInt32(_headerBuffer, 8));
            _transferActive = errorCode == 0;
            return new SysprogsSyncFileStream(this, fileStamp, errorCode);
        }
    }

    private class SysprogsSyncWriter : IDisposable
    {
        private ISyncEvents _events;

        private int _timeOffset;

        private SshShellChannel _channel;

        private byte[] _buffer = new byte[131072];

        private int _bufferedBytes;

        private readonly byte[] _chunk;

        private long _totalSent;

        public SysprogsSyncWriter(SshShellChannel sshShellChannel, int bufferSize, ISyncEvents syncEvents, int clockOffset)
        {
            _events = syncEvents;
            _timeOffset = clockOffset;
            _channel = sshShellChannel;
            this._chunk = new byte[bufferSize];
        }

        private void WriteBuffered(byte[] buffer, int offset = 0, int count = -1)
        {
            if (count == -1)
            {
                count = buffer.Length;
            }
            while (count > 0)
            {
                if (count >= _buffer.Length && _bufferedBytes == 0)
                {
                    _channel.Output.Write(buffer, offset, count);
                    break;
                }
                while (_bufferedBytes < _buffer.Length && count > 0)
                {
                    _buffer[_bufferedBytes++] = buffer[offset++];
                    count--;
                }
                if (_bufferedBytes == _buffer.Length)
                {
                    _channel.Output.Write(_buffer, 0, _bufferedBytes);
                    _bufferedBytes = 0;
                }
            }
        }

        public void Flush()
        {
            if (_bufferedBytes > 0)
            {
                _channel.Output.Write(_buffer, 0, _bufferedBytes);
                _bufferedBytes = 0;
            }
        }

        public void Dispose()
        {
            Flush();
        }

        public void WriteCommand(RemoteCommand remoteCommand, string arg)
        {
            this.WriteBuffered(new byte[1] { (byte)remoteCommand });
            WriteString(arg);
        }

        public void WriteCommand(RemoteCommand remoteCommand)
        {
            this.WriteBuffered(new byte[1] { (byte)remoteCommand });
        }

        public void WriteString(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            WriteInt(bytes.Length);
            this.WriteBuffered(bytes, 0, bytes.Length);
        }

        public void WriteInt(int value)
        {
            this.WriteBuffered(BitConverter.GetBytes(value));
        }

        public LocalFileSystem.FileStamp SendFile(string remotePath, LocalPath localPath, ref TransferStatistics transferStatistics)
        {
            using LocalFileSystem.NativeFile nativeFile = LocalFileSystem.OpenFile(localPath, LocalFileSystem.FileOpenMode.Open);
            long writeTime = nativeFile.GetLastWriteTime();
            LocalFileSystem.FileStamp fileStamp = new LocalFileSystem.FileStamp(0L, writeTime);
            long totalBytes = 0L;
            this.WriteCommand(SysprogsSyncEngine.RemoteCommand.SendFile);
            WriteInt(fileStamp.UnixSeconds + _timeOffset);
            int permissions = _events?.PermissionProvider?.GetFilePermissions(localPath) ?? 493;
            WriteInt(permissions);
            WriteString(remotePath);
            while (true)
            {
                try
                {
                    int bytesRead = nativeFile.Read(this._chunk, this._chunk.Length);
                    totalBytes += bytesRead;
                    WriteInt(bytesRead);
                    if (bytesRead == 0)
                    {
                        transferStatistics.CopiedFiles++;
                        return new LocalFileSystem.FileStamp(totalBytes, fileStamp.LastWriteFileTime);
                    }
                    this.WriteBuffered(this._chunk, 0, bytesRead);
                    _totalSent += bytesRead;
                    _events?.OnProgress(new SyncProgress
                    {
                        CurrentFile = remotePath,
                        Transferred = _totalSent,
                        Total = transferStatistics.TotalBytes,
                        Stage = SyncStage.Transferring
                    });
                }
                catch
                {
                    WriteInt(0);
                    throw;
                }
            }
        }
    }

    private class SysprogsSyncOutputParser
    {
        private ManualResetEvent _exitEvent = new ManualResetEvent(initialState: false);

        private SysprogsSyncEngine _engine;

        private LineSplitter _lineSplitter = new LineSplitter();

        private List<byte> _pending = new List<byte>();

        private bool _corrupted;

        private DateTime _lastActivity = DateTime.Now;

        public event Action<string> StderrLine;

        public event Action OutputCorrupt;

        public event Action<int, int> ActionFailedReceived;

        public SysprogsSyncOutputParser(SysprogsSyncEngine sysprogsSyncEngine, SshShellChannel sshShellChannel)
        {
            _engine = sysprogsSyncEngine;
            sshShellChannel.DataReceived += OnDataReceived;
            sshShellChannel.Finished += OnFinished;
            sshShellChannel.PollingEnabled = true;
        }

        private void OnFinished(object sender)
        {
            _exitEvent.Set();
        }

        internal void OnDataReceived(object sender, byte[] buffer, int offset, int count, int dataType)
        {
            try
            {
                if (dataType == 1)
                {
                    foreach (string line in this._lineSplitter.Feed(buffer, offset, count))
                    {
                        StderrLine?.Invoke(line);
                    }
                    return;
                }
                if (_corrupted)
                {
                    return;
                }
                for (int i = 0; i < count; i++)
                {
                    _pending.Add(buffer[offset + i]);
                }
                int idx = 0;
                while (idx < _pending.Count && !_corrupted)
                {
                    byte[] data = _pending.ToArray();
                    switch ((RemoteReply)_pending[idx])
                    {
                        case RemoteReply.ActionDone:
                            idx += 4;
                            break;
                        case RemoteReply.ActionFailed:
                            idx += 12;
                            if (idx <= _pending.Count)
                            {
                                int actionIndex = BitConverter.ToInt32(data, idx - 8);
                                int errorCode = BitConverter.ToInt32(data, idx - 4);
                                ActionFailedReceived?.Invoke(actionIndex, errorCode);
                            }
                            break;
                        default:
                            _corrupted = true;
                            OutputCorrupt?.Invoke();
                            idx++;
                            break;
                    }
                }
                if (idx > 0)
                {
                    _pending.RemoveRange(0, idx);
                }
            }
            catch (Exception exception)
            {
                StderrLine?.Invoke("Failed to process SysprogsSync output: " + exception.Message);
                _corrupted = true;
            }
        }

        public void WaitForExit(int timeoutMs)
        {
            _lastActivity = DateTime.Now;
            do
            {
                if (_exitEvent.WaitOne(timeoutMs / 2))
                {
                    return;
                }
            }
            while (!((DateTime.Now - _lastActivity).TotalMilliseconds > (double)timeoutMs));
            throw new Exception("Remote SysprogsSync session did not exit within the timeout");
        }
    }

    private SshSession _session;

    private SyncSettings _settings;

    private ISyncEvents _events;

    private readonly ILocalFileCache _cache;

    private readonly string _helperPath;

    private readonly SshShellChannel _channel;

    public SyncTuning Tuning;

    private bool _fileSetRead;

    private bool _uploadStarted;

    public override string ToString()
    {
        return "SysprogsSync synchronization engine";
    }

    public SysprogsSyncEngine(SshSession sshSession, SyncSettings syncSettings, ISyncEvents syncEvents, ILocalFileCache localFileCache, SyncTuning syncTuning, string helperPath, ISyncCustomization syncCustomization)
    {
        _session = sshSession;
        Tuning = syncTuning;
        this._settings = syncSettings;
        _events = syncEvents;
        _cache = localFileCache;
        this._helperPath = helperPath;
        IShellLauncher shellLauncher = null;
        if ((syncSettings.Options & SyncOptions.UseLauncher) != 0)
        {
            shellLauncher = syncCustomization?.GetLauncher(this._helperPath);
        }
        _channel = _session.OpenShellChannel(shellLauncher?.Command ?? this._helperPath);
        shellLauncher?.Prepare(_channel, this._helperPath);
        _channel.ReadStderrOnRead = true;
        WaitForReady(_channel);
    }

    public RemoteFileSet GetRemoteFileSet()
    {
        SendDownloadRequest(_channel);
        RemoteFileSet.RemoteFileSetReader remoteFileSetReader = new RemoteFileSet.RemoteFileSetReader(new SyncStreamReader(_channel), _settings.ClockOffset.Seconds);
        remoteFileSetReader.Warning += (QuickSyncWarning quickSyncWarning) =>
        {
            _events?.OnWarning(new SyncWarning
            {
                Warning = quickSyncWarning
            });
        };
        RemoteFileSet remoteFileSet = remoteFileSetReader.Read();
        if (_settings.Direction == SyncDirection.Upload)
        {
            _uploadStarted = true;
            return remoteFileSet;
        }
        _fileSetRead = true;
        return remoteFileSet;
    }

    public void Upload(List<SyncPlan.SyncAction> actions, List<LocalPath> localPaths, ref TransferStatistics transferStatistics)
    {
        if (!_uploadStarted)
        {
            List<byte> headerBytes = new List<byte>();
            headerBytes.AddRange(new byte[1] { 2 });
            SysprogsSyncEngine.AppendString(headerBytes, _settings.RemotePath);
            _channel.Input.Write(headerBytes.ToArray(), 0, headerBytes.Count);
            _uploadStarted = true;
        }
        LocalFileSystem.FileStamp[] fileStamps = null;
        Dictionary<int, KeyValuePair<string, string>> symlinkDict = null;
        if (_cache != null && _settings.QuickSyncOverwriteTrigger == QuickSyncOverwriteTrigger.LocalCache)
        {
            fileStamps = new LocalFileSystem.FileStamp[localPaths.Count];
            symlinkDict = new Dictionary<int, KeyValuePair<string, string>>();
        }
        SysprogsSyncOutputParser outputParser = new SysprogsSyncOutputParser(this, _channel);
        outputParser.StderrLine += (string line) =>
        {
            _events?.OnWarning(new SyncWarning
            {
                Warning = new QuickSyncWarning(QuickSyncWarningType.StderrLine, line, 0)
            });
        };
        outputParser.OutputCorrupt += () =>
        {
            _events?.OnWarning(new SyncWarning
            {
                Warning = new QuickSyncWarning(QuickSyncWarningType.RemoteOutputCorrupt, null, 0)
            });
        };
        outputParser.ActionFailedReceived += (int actionIdx, int errorCode) =>
        {
            if (actionIdx >= 0 && actionIdx < actions.Count)
            {
                if (fileStamps != null)
                {
                    if (actions[actionIdx].Type == SyncPlan.ActionType.TransferFile)
                    {
                        fileStamps[actions[actionIdx].Argument] = default;
                    }
                    else if (actions[actionIdx].Type == SyncPlan.ActionType.CreateRemoteSymlink)
                    {
                        lock (symlinkDict)
                        {
                            symlinkDict.Remove(actionIdx);
                        }
                    }
                }
                _events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(GetWarningType(actions[actionIdx].Type), actions[actionIdx].Path, errorCode)
                });
            }
        };
        using (SysprogsSyncWriter syncWriter = new SysprogsSyncWriter(_channel, Tuning.BufferSize, _events, _settings.ClockOffset.Seconds))
        {
            int currentActionIdx = -1;
            foreach (SyncPlan.SyncAction action in actions)
            {
                currentActionIdx++;
                switch (action.Type)
                {
                    case SyncPlan.ActionType.CreateAndEnterDirectory:
                        syncWriter.WriteCommand(RemoteCommand.CreateAndEnterDirectory, action.Path);
                        break;
                    case SyncPlan.ActionType.EnterDirectory:
                        syncWriter.WriteCommand(RemoteCommand.EnterDirectory, action.Path);
                        break;
                    case SyncPlan.ActionType.GoToParentDirectory:
                        syncWriter.WriteCommand(RemoteCommand.GoToParent);
                        break;
                    case SyncPlan.ActionType.TransferFile:
                        try
                        {
                            LocalFileSystem.FileStamp fileStamp = syncWriter.SendFile(action.Path, localPaths[action.Argument], ref transferStatistics);
                            if (fileStamps != null)
                            {
                                fileStamps[action.Argument] = fileStamp;
                            }
                        }
                        catch (Exception exception)
                        {
                            _events?.OnWarning(new SyncWarning
                            {
                                Exception = exception,
                                Warning = new QuickSyncWarning(QuickSyncWarningType.CannotReadLocalFile, localPaths[action.Argument].FullPath, 0)
                            });
                        }
                        break;
                    case SyncPlan.ActionType.DeleteTargetFile:
                        syncWriter.WriteCommand(RemoteCommand.DeleteFile, action.Path);
                        break;
                    case SyncPlan.ActionType.DeleteEmptyTargetDirectory:
                        syncWriter.WriteCommand(RemoteCommand.DeleteEmptyDirectory, action.Path);
                        break;
                    case SyncPlan.ActionType.DeleteTargetDirectoryRecursively:
                        syncWriter.WriteCommand(RemoteCommand.DeleteDirectoryRecursively, action.Path);
                        break;
                    case SyncPlan.ActionType.CreateRemoteSymlink:
                        {
                            KeyValuePair<string, string> symlinkPair = StringUtils.SplitAtFirst(action.Path, '\n');
                            if (symlinkPair.Key == null || symlinkPair.Value == null)
                            {
                                throw new Exception("Invalid queued symlink: " + action.Path);
                            }
                            string fileName = RemotePathUtils.GetFileName(symlinkPair.Key);
                            if (symlinkDict != null)
                            {
                                lock (symlinkDict)
                                {
                                    symlinkDict[currentActionIdx] = symlinkPair;
                                }
                            }
                            syncWriter.WriteCommand(RemoteCommand.CreateSymlink, fileName);
                            syncWriter.WriteString(symlinkPair.Value);
                            break;
                        }
                    case SyncPlan.ActionType.PrintStderrMessage:
                        syncWriter.WriteCommand(RemoteCommand.PrintMessage, action.Path);
                        break;
                }
            }
            syncWriter.WriteCommand(RemoteCommand.End);
            syncWriter.Flush();
            outputParser.WaitForExit(Tuning.ExitTimeoutMs);
        }
        if (fileStamps == null)
        {
            return;
        }
        for (int i = 0; i < fileStamps.Length; i++)
        {
            if (fileStamps[i].HasTimestamp)
            {
                _cache.StoreFileStamp(i, fileStamps[i]);
            }
        }
        LocalPath baseLocalPath = LocalPath.FromUserPath(_settings.LocalPath);
        foreach (KeyValuePair<int, KeyValuePair<string, string>> kvp in symlinkDict)
        {
            _cache.StoreSymlink(baseLocalPath.Combine(kvp.Value.Key), kvp.Value.Value);
        }
    }

    public static QuickSyncWarningType GetWarningType(SyncPlan.ActionType actionType)
    {
        switch (actionType)
        {
            case SyncPlan.ActionType.CreateAndEnterDirectory:
                return QuickSyncWarningType.CannotCreateTargetDirectory;
            case SyncPlan.ActionType.EnterDirectory:
            case SyncPlan.ActionType.GoToParentDirectory:
                return QuickSyncWarningType.UnreadableRemoteDirectory;
            case SyncPlan.ActionType.TransferFile:
                return QuickSyncWarningType.CannotCreateTargetFile;
            case SyncPlan.ActionType.DeleteTargetFile:
                return QuickSyncWarningType.CannotDeleteTargetFile;
            case SyncPlan.ActionType.DeleteEmptyTargetDirectory:
            case SyncPlan.ActionType.DeleteTargetDirectoryRecursively:
                return QuickSyncWarningType.CannotRemoveTargetDirectory;
            case SyncPlan.ActionType.CreateRemoteSymlink:
                return QuickSyncWarningType.CreateSymlinkFailed;
            default:
                return QuickSyncWarningType.UnknownRemoteAction;
        }
    }

    public IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> actions)
    {
        if (!_fileSetRead)
        {
            throw new Exception("Remote SysprogsSync instance is not ready to receive the file list");
        }
        List<byte> bytesList = new List<byte>();
        foreach (SyncPlan.SyncAction action in actions)
        {
            if (action.Type == SyncPlan.ActionType.TransferFile)
            {
                bytesList.AddRange(BitConverter.GetBytes(action.Argument));
            }
        }
        int bufferSize = Tuning.BufferSize;
        _channel.Input.Write(BitConverter.GetBytes(bufferSize), 0, 4);
        _channel.Input.Write(BitConverter.GetBytes(bytesList.Count), 0, 4);
        _channel.Input.Write(bytesList.ToArray(), 0, bytesList.Count);
        return new SysprogsSyncFileSource(_channel);
    }

    private static void WaitForReady(SshShellChannel sshShellChannel)
    {
        byte[] readyMarker = Encoding.UTF8.GetBytes("SysprogsSync v1.2 Ready\n");
        List<byte> readBytes = new List<byte>();
        byte[] singleByte = new byte[1];
        do
        {
            if (sshShellChannel.Output.Read(singleByte, 0, 1) != 1)
            {
                throw new Exception("Remote SysprogsSync instance exited unexpectedly");
            }
            readBytes.Add(singleByte[0]);
            while (readBytes.Count > readyMarker.Length)
            {
                readBytes.RemoveAt(0);
            }
        }
        while (!readBytes.SequenceEqual(readyMarker));
    }

    private void SendDownloadRequest(SshShellChannel sshShellChannel)
    {
        List<byte> list = new List<byte>();
        SessionMode sessionMode = ((_settings.Direction == SyncDirection.Upload) ? SessionMode.Upload : SessionMode.Download);
        list.Add((byte)sessionMode);
        SessionFlags sessionFlags = SessionFlags.None;
        if ((_settings.Options & SyncOptions.OneFileSystem) == SyncOptions.OneFileSystem)
        {
            sessionFlags |= SessionFlags.OneFileSystem;
        }
        if (_settings.Direction == SyncDirection.Upload)
        {
            sessionFlags |= SessionFlags.UploadMode;
        }
        list.AddRange(BitConverter.GetBytes((int)sessionFlags));
        list.AddRange(BitConverter.GetBytes(_settings.DirectoryFilters?.Length ?? QuickSyncFilter.AllArray.Length));
        list.AddRange(BitConverter.GetBytes(_settings.FileFilters?.Length ?? QuickSyncFilter.AllArray.Length));
        SysprogsSyncEngine.AppendString(list, _settings.RemotePath);
        QuickSyncFilter[] dirFilters = _settings.DirectoryFilters ?? QuickSyncFilter.AllArray;
        foreach (QuickSyncFilter filter in dirFilters)
        {
            AppendFilter(list, filter);
        }
        QuickSyncFilter[] fileFilters = _settings.FileFilters ?? QuickSyncFilter.AllArray;
        foreach (QuickSyncFilter filter in fileFilters)
        {
            AppendFilter(list, filter);
        }
        sshShellChannel.Input.Write(list.ToArray(), 0, list.Count);
    }

    private static void AppendFilter(List<byte> list, QuickSyncFilter quickSyncFilter)
    {
        list.AddRange(BitConverter.GetBytes((quickSyncFilter.Action == QuickSyncAction.Include) ? 1 : 0));
        SysprogsSyncEngine.AppendString(list, quickSyncFilter.Regex?.ToString() ?? ".*");
    }

    private static void AppendString(List<byte> list, string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        list.AddRange(BitConverter.GetBytes(bytes.Length));
        list.AddRange(bytes);
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
