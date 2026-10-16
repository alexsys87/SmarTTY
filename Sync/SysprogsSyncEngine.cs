using ManagedLibSSH2;
using ManagedLibSSH2.QuickSync;
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
        public SysprogsSyncException(string text)
            : base(text)
        {
        }
    }

    private enum SessionMode
    {
        Download,
        Upload,
        Value2
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

            public SysprogsSyncFileStream(SysprogsSyncFileSource sysprogsSyncFileSource, LocalFileSystem.FileStamp fileStamp, int value2)
            {
                this._source = sysprogsSyncFileSource;
                ModifiedTime = fileStamp.LastWriteFileTime;
                this.ErrorCode = value2;
                if (value2 != 0)
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
                int num = _source._reader.ReadInt32();
                if (buffer == null || buffer.Length < num)
                {
                    buffer = new byte[num];
                }
                if (num == 0)
                {
                    _finished = true;
                    _source._transferActive = false;
                }
                _source._reader.ReadBytes(buffer, num);
                return num;
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
            int num = _reader.ReadInt32();
            if (num != 1162760004)
            {
                throw new Exception("Expected end of remote SysprogsSync stream, got " + num);
            }
        }

        public IRemoteFileStream OpenFile(int value2)
        {
            if (_transferActive)
            {
                throw new Exception("The previous file transfer is still in progress");
            }
            this._reader.ReadBytes(_headerBuffer, 12);
            int num = BitConverter.ToInt32(_headerBuffer, 0);
            if (num != value2)
            {
                throw new Exception($"Remote SysprogsSync sent unexpected fileID: {num}, expected {value2}");
            }
            int num2 = BitConverter.ToInt32(_headerBuffer, 4);
            LocalFileSystem.FileStamp fileStamp = new LocalFileSystem.FileStamp(0L, BitConverter.ToInt32(_headerBuffer, 8));
            _transferActive = num2 == 0;
            return new SysprogsSyncFileStream(this, fileStamp, num2);
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

        public SysprogsSyncWriter(SshShellChannel sshShellChannel, int value2, ISyncEvents syncEvents, int value3)
        {
            _events = syncEvents;
            _timeOffset = value3;
            _channel = sshShellChannel;
            this._chunk = new byte[value2];
        }

        private void WriteBuffered(byte[] buffer, int value2 = 0, int value3 = -1)
        {
            if (value3 == -1)
            {
                value3 = buffer.Length;
            }
            while (value3 > 0)
            {
                if (value3 >= _buffer.Length && _bufferedBytes == 0)
                {
                    _channel.Output.Write(buffer, value2, value3);
                    break;
                }
                while (_bufferedBytes < _buffer.Length && value3 > 0)
                {
                    _buffer[_bufferedBytes++] = buffer[value2++];
                    value3--;
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

        public void WriteCommand(RemoteCommand remoteCommand, string text)
        {
            this.WriteBuffered(new byte[1] { (byte)remoteCommand });
            WriteString(text);
        }

        public void WriteCommand(RemoteCommand remoteCommand)
        {
            this.WriteBuffered(new byte[1] { (byte)remoteCommand });
        }

        public void WriteString(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            WriteInt(bytes.Length);
            this.WriteBuffered(bytes, 0, bytes.Length);
        }

        public void WriteInt(int value2)
        {
            this.WriteBuffered(BitConverter.GetBytes(value2));
        }

        public LocalFileSystem.FileStamp SendFile(string text, LocalPath localPath, ref TransferStatistics transferStatistics)
        {
            using LocalFileSystem.NativeFile nativeFile = LocalFileSystem.OpenFile(localPath, LocalFileSystem.FileOpenMode.Open);
            long num = nativeFile.GetLastWriteTime();
            LocalFileSystem.FileStamp fileStamp = new LocalFileSystem.FileStamp(0L, num);
            long num2 = 0L;
            this.WriteCommand(SysprogsSyncEngine.RemoteCommand.SendFile);
            WriteInt(fileStamp.UnixSeconds + _timeOffset);
            int num3 = _events?.PermissionProvider?.GetFilePermissions(localPath) ?? 493;
            WriteInt(num3);
            WriteString(text);
            while (true)
            {
                try
                {
                    int num4 = nativeFile.Read(this._chunk, this._chunk.Length);
                    num2 += num4;
                    WriteInt(num4);
                    if (num4 == 0)
                    {
                        transferStatistics.CopiedFiles++;
                        return new LocalFileSystem.FileStamp(num2, fileStamp.LastWriteFileTime);
                    }
                    this.WriteBuffered(this._chunk, 0, num4);
                    _totalSent += num4;
                    _events?.OnProgress(new SyncProgress
                    {
                        CurrentFile = text,
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

        private void OnFinished(object obj)
        {
            _exitEvent.Set();
        }

        internal void OnDataReceived(object obj, byte[] buffer, int value2, int value3, int value4)
        {
            try
            {
                if (value4 == 1)
                {
                    foreach (string item in this._lineSplitter.Feed(buffer, value2, value3))
                    {
                        StderrLine?.Invoke(item);
                    }
                    return;
                }
                if (_corrupted)
                {
                    return;
                }
                for (int num = 0; num < value3; num++)
                {
                    _pending.Add(buffer[value2 + num]);
                }
                int num2 = 0;
                while (num2 < _pending.Count && !_corrupted)
                {
                    byte[] value = _pending.ToArray();
                    switch ((RemoteReply)_pending[num2])
                    {
                        case RemoteReply.ActionDone:
                            num2 += 4;
                            break;
                        case RemoteReply.ActionFailed:
                            num2 += 12;
                            if (num2 <= _pending.Count)
                            {
                                int arg = BitConverter.ToInt32(value, num2 - 8);
                                int arg2 = BitConverter.ToInt32(value, num2 - 4);
                                ActionFailedReceived?.Invoke(arg, arg2);
                            }
                            break;
                        default:
                            _corrupted = true;
                            OutputCorrupt?.Invoke();
                            num2++;
                            break;
                    }
                }
                if (num2 > 0)
                {
                    _pending.RemoveRange(0, num2);
                }
            }
            catch (Exception ex)
            {
                StderrLine?.Invoke("Failed to process SysprogsSync output: " + ex.Message);
                _corrupted = true;
            }
        }

        public void WaitForExit(int value2)
        {
            _lastActivity = DateTime.Now;
            do
            {
                if (_exitEvent.WaitOne(value2 / 2))
                {
                    return;
                }
            }
            while (!((DateTime.Now - _lastActivity).TotalMilliseconds > (double)value2));
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

    public SysprogsSyncEngine(SshSession sshSession, SyncSettings syncSettings, ISyncEvents syncEvents, ILocalFileCache localFileCache, SyncTuning syncTuning, string text, ISyncCustomization syncCustomization)
    {
        _session = sshSession;
        Tuning = syncTuning;
        this._settings = syncSettings;
        _events = syncEvents;
        _cache = localFileCache;
        this._helperPath = text;
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
        RemoteFileSet result = remoteFileSetReader.Read();
        if (_settings.Direction == SyncDirection.Upload)
        {
            _uploadStarted = true;
            return result;
        }
        _fileSetRead = true;
        return result;
    }

    public void Upload(List<SyncPlan.SyncAction> list2, List<LocalPath> list3, ref TransferStatistics transferStatistics)
    {
        if (!_uploadStarted)
        {
            List<byte> list = new List<byte>();
            list.AddRange(new byte[1] { 2 });
            SysprogsSyncEngine.AppendString(list, _settings.RemotePath);
            _channel.Input.Write(list.ToArray(), 0, list.Count);
            _uploadStarted = true;
        }
        LocalFileSystem.FileStamp[] items = null;
        Dictionary<int, KeyValuePair<string, string>> dictionary = null;
        if (_cache != null && _settings.QuickSyncOverwriteTrigger == QuickSyncOverwriteTrigger.LocalCache)
        {
            items = new LocalFileSystem.FileStamp[list3.Count];
            dictionary = new Dictionary<int, KeyValuePair<string, string>>();
        }
        SysprogsSyncOutputParser sysprogsSyncOutputParser = new SysprogsSyncOutputParser(this, _channel);
        sysprogsSyncOutputParser.StderrLine += (string text2) =>
        {
            _events?.OnWarning(new SyncWarning
            {
                Warning = new QuickSyncWarning(QuickSyncWarningType.StderrLine, text2, 0)
            });
        };
        sysprogsSyncOutputParser.OutputCorrupt += () =>
        {
            _events?.OnWarning(new SyncWarning
            {
                Warning = new QuickSyncWarning(QuickSyncWarningType.RemoteOutputCorrupt, null, 0)
            });
        };
        sysprogsSyncOutputParser.ActionFailedReceived += (int num3, int num4) =>
        {
            if (num3 >= 0 && num3 < list2.Count)
            {
                if (items != null)
                {
                    if (list2[num3].Type == SyncPlan.ActionType.TransferFile)
                    {
                        items[list2[num3].Argument] = default;
                    }
                    else if (list2[num3].Type == SyncPlan.ActionType.CreateRemoteSymlink)
                    {
                        lock (dictionary)
                        {
                            dictionary.Remove(num3);
                        }
                    }
                }
                _events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(GetWarningType(list2[num3].Type), list2[num3].Path, num4)
                });
            }
        };
        using (SysprogsSyncWriter sysprogsSyncWriter = new SysprogsSyncWriter(_channel, Tuning.BufferSize, _events, _settings.ClockOffset.Seconds))
        {
            int num = -1;
            foreach (SyncPlan.SyncAction item in list2)
            {
                num++;
                switch (item.Type)
                {
                    case SyncPlan.ActionType.CreateAndEnterDirectory:
                        sysprogsSyncWriter.WriteCommand(RemoteCommand.CreateAndEnterDirectory, item.Path);
                        break;
                    case SyncPlan.ActionType.EnterDirectory:
                        sysprogsSyncWriter.WriteCommand(RemoteCommand.EnterDirectory, item.Path);
                        break;
                    case SyncPlan.ActionType.GoToParentDirectory:
                        sysprogsSyncWriter.WriteCommand(RemoteCommand.GoToParent);
                        break;
                    case SyncPlan.ActionType.TransferFile:
                        try
                        {
                            LocalFileSystem.FileStamp fileStamp = sysprogsSyncWriter.SendFile(item.Path, list3[item.Argument], ref transferStatistics);
                            if (items != null)
                            {
                                items[item.Argument] = fileStamp;
                            }
                        }
                        catch (Exception ex)
                        {
                            _events?.OnWarning(new SyncWarning
                            {
                                Exception = ex,
                                Warning = new QuickSyncWarning(QuickSyncWarningType.CannotReadLocalFile, list3[item.Argument].FullPath, 0)
                            });
                        }
                        break;
                    case SyncPlan.ActionType.DeleteTargetFile:
                        sysprogsSyncWriter.WriteCommand(RemoteCommand.DeleteFile, item.Path);
                        break;
                    case SyncPlan.ActionType.DeleteEmptyTargetDirectory:
                        sysprogsSyncWriter.WriteCommand(RemoteCommand.DeleteEmptyDirectory, item.Path);
                        break;
                    case SyncPlan.ActionType.DeleteTargetDirectoryRecursively:
                        sysprogsSyncWriter.WriteCommand(RemoteCommand.DeleteDirectoryRecursively, item.Path);
                        break;
                    case SyncPlan.ActionType.CreateRemoteSymlink:
                        {
                            KeyValuePair<string, string> value = StringUtils.SplitAtFirst(item.Path, '\n');
                            if (value.Key == null || value.Value == null)
                            {
                                throw new Exception("Invalid queued symlink: " + item.Path);
                            }
                            string text = RemotePathUtils.GetFileName(value.Key);
                            if (dictionary != null)
                            {
                                lock (dictionary)
                                {
                                    dictionary[num] = value;
                                }
                            }
                            sysprogsSyncWriter.WriteCommand(RemoteCommand.CreateSymlink, text);
                            sysprogsSyncWriter.WriteString(value.Value);
                            break;
                        }
                    case SyncPlan.ActionType.PrintStderrMessage:
                        sysprogsSyncWriter.WriteCommand(RemoteCommand.PrintMessage, item.Path);
                        break;
                }
            }
            sysprogsSyncWriter.WriteCommand(RemoteCommand.End);
            sysprogsSyncWriter.Flush();
            sysprogsSyncOutputParser.WaitForExit(Tuning.ExitTimeoutMs);
        }
        if (items == null)
        {
            return;
        }
        for (int num2 = 0; num2 < items.Length; num2++)
        {
            if (items[num2].HasTimestamp)
            {
                _cache.StoreFileStamp(num2, items[num2]);
            }
        }
        LocalPath localPath = LocalPath.FromUserPath(_settings.LocalPath);
        foreach (KeyValuePair<int, KeyValuePair<string, string>> item2 in dictionary)
        {
            _cache.StoreSymlink(localPath.Combine(item2.Value.Key), item2.Value.Value);
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

    public IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> items)
    {
        if (!_fileSetRead)
        {
            throw new Exception("Remote SysprogsSync instance is not ready to receive the file list");
        }
        List<byte> list = new List<byte>();
        foreach (SyncPlan.SyncAction item in items)
        {
            if (item.Type == SyncPlan.ActionType.TransferFile)
            {
                list.AddRange(BitConverter.GetBytes(item.Argument));
            }
        }
        int value = Tuning.BufferSize;
        _channel.Input.Write(BitConverter.GetBytes(value), 0, 4);
        _channel.Input.Write(BitConverter.GetBytes(list.Count), 0, 4);
        _channel.Input.Write(list.ToArray(), 0, list.Count);
        return new SysprogsSyncFileSource(_channel);
    }

    private static void WaitForReady(SshShellChannel sshShellChannel)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("SysprogsSync v1.2 Ready\n");
        List<byte> list = new List<byte>();
        byte[] array = new byte[1];
        do
        {
            if (sshShellChannel.Output.Read(array, 0, 1) != 1)
            {
                throw new Exception("Remote SysprogsSync instance exited unexpectedly");
            }
            list.Add(array[0]);
            while (list.Count > bytes.Length)
            {
                list.RemoveAt(0);
            }
        }
        while (!list.SequenceEqual(bytes));
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
        QuickSyncFilter[] array = _settings.DirectoryFilters ?? QuickSyncFilter.AllArray;
        foreach (QuickSyncFilter quickSyncFilter in array)
        {
            AppendFilter(list, quickSyncFilter);
        }
        array = _settings.FileFilters ?? QuickSyncFilter.AllArray;
        foreach (QuickSyncFilter quickSyncFilter2 in array)
        {
            AppendFilter(list, quickSyncFilter2);
        }
        sshShellChannel.Input.Write(list.ToArray(), 0, list.Count);
    }

    private static void AppendFilter(List<byte> list, QuickSyncFilter quickSyncFilter)
    {
        list.AddRange(BitConverter.GetBytes((quickSyncFilter.Action == QuickSyncAction.Include) ? 1 : 0));
        SysprogsSyncEngine.AppendString(list, quickSyncFilter.Regex?.ToString() ?? ".*");
    }

    private static void AppendString(List<byte> list, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        list.AddRange(BitConverter.GetBytes(bytes.Length));
        list.AddRange(bytes);
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
