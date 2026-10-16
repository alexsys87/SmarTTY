using ManagedLibSSH2;
using ManagedLibSSH2.QuickSync;
using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmarTTY.Sync;

internal abstract class CommandSyncEngine : ISyncEngine, IDisposable
{
    protected struct DownloadEntry
    {
        public string RemotePath;

        public int ModifiedSeconds;
    }

    private class RemoteTreeScanner : IDisposable
    {
        private class ScanDirectory
        {
            public RemoteFileSet.RemoteDirectory RemoteDirectory;

            public RelativeRemotePath RelativePath;

            public int DeviceId;

            public string FullPath;

            public bool IsIncluded;

            public ScanDirectory(RemoteFileSet.RemoteDirectory remoteDirectory, RelativeRemotePath relativeRemotePath, string text, int value2 = 0)
            {
                this.RemoteDirectory = remoteDirectory;
                this.DeviceId = value2;
                RelativePath = relativeRemotePath;
                FullPath = text;
            }

            public ScanDirectory CreateChild(string text, int value2, string text2 = null)
            {
                return new ScanDirectory(new RemoteFileSet.RemoteDirectory(this.RemoteDirectory, text), RelativePath.Combine(text), text2 ?? (FullPath.TrimEnd('/') + "/" + text), value2);
            }
        }

        private struct PendingSymlink
        {
            public ScanDirectory Parent;

            public string Name;

            public string Target;

            public string ResolvedPath => RemotePathUtils.NormalizeUnix(RemotePathUtils.Combine(Parent.FullPath, Target));

            public PendingSymlink(ScanDirectory scanDirectory, string text, string text2)
            {
                Parent = scanDirectory;
                this.Name = text;
                this.Target = text2;
            }
        }

        private SshSession _session;

        private readonly CommandSyncEngine _engine;

        private string _rootPath;

        private readonly SyncSettings _settings;

        private Dictionary<string, ScanDirectory> _visited = new Dictionary<string, ScanDirectory>();

        private StatCommandBuilder.StatFlavor _flavor;

        public List<DownloadEntry> Files = new List<DownloadEntry>();

        public RemoteTreeScanner(string text, CommandSyncEngine commandSyncEngine)
        {
            this._session = commandSyncEngine._session;
            _engine = commandSyncEngine;
            this._rootPath = text;
            _settings = commandSyncEngine._settings;
        }

        public void Dispose()
        {
        }

        private List<string> RunCommand(string text, bool flag = true)
        {
            return _engine.RunCommand(text, flag);
        }

        public void Scan(RemoteFileSet.RemoteDirectory remoteDirectory)
        {
            Files.Add(default);
            if (RunCommand("test -d \"" + this._rootPath + "\"") == null)
            {
                return;
            }
            if (_flavor == StatCommandBuilder.StatFlavor.Unknown)
            {
                try
                {
                    _flavor = StatCommandBuilder.DetectFlavor((string text2) =>
                    {
                        _engine._events?.OnLog(new SyncLogMessage.RunCommandMessage(text2));
                        return _session.RunCommandChecked(text2, requireSuccess: true);
                    });
                }
                catch
                {
                    _flavor = StatCommandBuilder.StatFlavor.Unknown;
                }
            }
            StatCommandBuilder statCommandBuilder = new StatCommandBuilder(_flavor);
            string text = statCommandBuilder.GetStatCommand(this._rootPath, flag: true);
            StatCommandBuilder.StatEntry statEntry2 = RunCommand(text)?.Select((string text2) => statCommandBuilder.ParseEntry(text2)).FirstOrDefault((StatCommandBuilder.StatEntry statEntry) => statEntry.Name != null) ?? default(StatCommandBuilder.StatEntry);
            List<ScanDirectory> list = new List<ScanDirectory>();
            List<PendingSymlink> list2 = new List<PendingSymlink>();
            list.Add(new ScanDirectory(remoteDirectory, new RelativeRemotePath(""), this._rootPath, statEntry2.DeviceId));
            while (list.Count > 0 || list2.Count > 0)
            {
                list = ScanDirectories(statCommandBuilder, list, list2);
                _engine._events?.OnProgress(new SyncProgress
                {
                    Stage = SyncStage.BuildingFileList
                });
                if (list.Count == 0)
                {
                    ResolveSymlinks(list2, statCommandBuilder, list);
                    list2.Clear();
                }
            }
        }

        private void ResolveSymlinks(List<PendingSymlink> list4, StatCommandBuilder statCommandBuilder, List<ScanDirectory> list5)
        {
            string text = Guid.NewGuid().ToString();
            MemoryStream memoryStream = new MemoryStream();
            using StreamWriter streamWriter = new StreamWriter(memoryStream)
            {
                NewLine = "\n"
            };
            streamWriter.WriteLine("echo " + text);
            for (int num = 0; num < list4.Count; num++)
            {
                streamWriter.WriteLine($"echo ---link#{num}");
                streamWriter.WriteLine(statCommandBuilder.GetStatCommand(list4[num].ResolvedPath, flag: true) + $" || echo badlink:{num}");
            }
            streamWriter.Flush();
            List<string> list = this._engine.RunScript(text, memoryStream.ToArray());
            bool flag = false;
            int result = -1;
            foreach (string item2 in list)
            {
                if (!flag)
                {
                    if (!(item2 != text))
                    {
                        flag = true;
                    }
                }
                else if (item2.StartsWith("---link#"))
                {
                    if (!int.TryParse(item2.Substring(8), out result))
                    {
                        result = -1;
                    }
                }
                else
                {
                    if (result < 0 || result >= list4.Count)
                    {
                        continue;
                    }
                    if (item2.StartsWith("badlink:") && int.TryParse(item2.Substring(8), out var result2) && result2 == result)
                    {
                        this._engine._events?.OnWarning(new SyncWarning
                        {
                            Warning = new QuickSyncWarning(QuickSyncWarningType.UnreadableSourceSymlink, list4[result2].Parent.RelativePath.Combine(list4[result2].Name).Path, 0)
                        });
                        continue;
                    }
                    PendingSymlink pendingSymlink = list4[result];
                    StatCommandBuilder.StatEntry statEntry = statCommandBuilder.ParseEntry(item2);
                    statEntry.ModifiedTime -= _settings.ClockOffset.Seconds;
                    if (statEntry.Kind == StatCommandBuilder.StatEntryKind.Directory)
                    {
                        if (CommandSyncEngine.IsIncluded(pendingSymlink.Parent.RelativePath.Combine(pendingSymlink.Name).Path, _settings.DirectoryFilters))
                        {
                            ScanDirectory value;
                            if (this._engine.IsUpload)
                            {
                                pendingSymlink.Parent.RemoteDirectory.Symlinks.Add(new RemoteFileSet.RemoteDirectory.RemoteSymlink
                                {
                                    IsDirectory = true,
                                    Name = pendingSymlink.Name,
                                    Target = pendingSymlink.Target
                                });
                            }
                            else if (_visited.TryGetValue(pendingSymlink.ResolvedPath, out value))
                            {
                                List<RemoteFileSet.RemoteDirectory.RemoteSymlink> list2 = pendingSymlink.Parent.RemoteDirectory.Symlinks;
                                RemoteFileSet.RemoteDirectory.RemoteSymlink item = new RemoteFileSet.RemoteDirectory.RemoteSymlink
                                {
                                    IsDirectory = true,
                                    Name = pendingSymlink.Name
                                };
                                RelativeRemotePath relativeRemotePath = pendingSymlink.Parent.RelativePath;
                                string text2 = "/" + relativeRemotePath.ToString();
                                relativeRemotePath = value.RelativePath;
                                item.Target = RemotePathUtils.GetRelativePath(text2, "/" + relativeRemotePath.ToString(), flag: false);
                                list2.Add(item);
                            }
                            else
                            {
                                ScanDirectory scanDirectory = pendingSymlink.Parent.CreateChild(pendingSymlink.Name, statEntry.DeviceId, pendingSymlink.ResolvedPath);
                                pendingSymlink.Parent.RemoteDirectory.SubDirectories.Add(scanDirectory.RemoteDirectory);
                                list5.Add(scanDirectory);
                            }
                        }
                    }
                    else if (statEntry.Kind == StatCommandBuilder.StatEntryKind.File && pendingSymlink.Parent.IsIncluded && CommandSyncEngine.IsIncluded(RemotePathUtils.GetFileName(pendingSymlink.Name), _settings.FileFilters))
                    {
                        string text3 = pendingSymlink.ResolvedPath;
                        ScanDirectory value2;
                        if (this._engine.IsUpload)
                        {
                            pendingSymlink.Parent.RemoteDirectory.Symlinks.Add(new RemoteFileSet.RemoteDirectory.RemoteSymlink
                            {
                                IsDirectory = false,
                                Name = pendingSymlink.Name,
                                Target = pendingSymlink.Target
                            });
                        }
                        else if (_visited.TryGetValue(RemotePathUtils.GetDirectoryName(text3), out value2) && value2.IsIncluded && CommandSyncEngine.IsIncluded(RemotePathUtils.GetFileName(pendingSymlink.Target), _settings.FileFilters))
                        {
                            List<RemoteFileSet.RemoteDirectory.RemoteSymlink> list3 = pendingSymlink.Parent.RemoteDirectory.Symlinks;
                            RemoteFileSet.RemoteDirectory.RemoteSymlink item = new RemoteFileSet.RemoteDirectory.RemoteSymlink
                            {
                                IsDirectory = false,
                                Name = pendingSymlink.Name
                            };
                            RelativeRemotePath relativeRemotePath2 = pendingSymlink.Parent.RelativePath;
                            string text4 = "/" + relativeRemotePath2.ToString();
                            relativeRemotePath2 = value2.RelativePath;
                            item.Target = RemotePathUtils.GetRelativePath(text4, "/" + relativeRemotePath2.ToString() + "/" + RemotePathUtils.GetFileName(pendingSymlink.Target), flag: false);
                            list3.Add(item);
                        }
                        else
                        {
                            pendingSymlink.Parent.RemoteDirectory.Files.Add(new RemoteFileSet.RemoteFileEntry
                            {
                                Id = Files.Count,
                                Name = pendingSymlink.Name,
                                Size = statEntry.Size,
                                ModifiedSeconds = statEntry.ModifiedSeconds
                            });
                            Files.Add(new DownloadEntry
                            {
                                RemotePath = text3,
                                ModifiedSeconds = statEntry.ModifiedSeconds
                            });
                        }
                    }
                }
            }
        }

        private List<ScanDirectory> ScanDirectories(StatCommandBuilder statCommandBuilder, List<ScanDirectory> list4, List<PendingSymlink> list5)
        {
            List<ScanDirectory> list = new List<ScanDirectory>();
            string text = Guid.NewGuid().ToString();
            MemoryStream memoryStream = new MemoryStream();
            using StreamWriter streamWriter = new StreamWriter(memoryStream)
            {
                NewLine = "\n"
            };
            streamWriter.WriteLine("echo " + text);
            foreach (ScanDirectory item2 in list4)
            {
                string text2 = item2.FullPath;
                _visited[text2] = item2;
                streamWriter.WriteLine("echo --dir:" + text2);
                streamWriter.WriteLine(statCommandBuilder.GetListCommand(text2));
            }
            streamWriter.Flush();
            List<string> list2 = this._engine.RunScript(text, memoryStream.ToArray());
            bool flag = false;
            ScanDirectory scanDirectory = null;
            foreach (string item3 in list2)
            {
                if (!flag)
                {
                    if (!(item3 != text))
                    {
                        flag = true;
                    }
                }
                else if (item3.StartsWith("--dir:"))
                {
                    scanDirectory = _visited.GetOrDefault(item3.Substring(6).Trim());
                    if (scanDirectory != null)
                    {
                        scanDirectory.IsIncluded = CommandSyncEngine.IsIncluded((scanDirectory.RelativePath.Path + "/.").TrimStart('/'), _settings.DirectoryFilters);
                    }
                }
                else
                {
                    if (scanDirectory == null)
                    {
                        continue;
                    }
                    StatCommandBuilder.StatEntry statEntry = statCommandBuilder.ParseEntry(item3);
                    if (statEntry.Name == "." || statEntry.Name == "..")
                    {
                        continue;
                    }
                    string key = scanDirectory.FullPath + "/" + statEntry.Name;
                    switch (statEntry.Kind)
                    {
                        case StatCommandBuilder.StatEntryKind.File:
                            if (scanDirectory.IsIncluded && CommandSyncEngine.IsIncluded(statEntry.Name, _settings.FileFilters))
                            {
                                scanDirectory.RemoteDirectory.Files.Add(new RemoteFileSet.RemoteFileEntry
                                {
                                    Id = Files.Count,
                                    Name = statEntry.Name,
                                    Size = statEntry.Size,
                                    ModifiedSeconds = statEntry.ModifiedSeconds
                                });
                                Files.Add(new DownloadEntry
                                {
                                    RemotePath = key,
                                    ModifiedSeconds = statEntry.ModifiedSeconds
                                });
                            }
                            break;
                        case StatCommandBuilder.StatEntryKind.Symlink:
                            if (statEntry.LinkTarget != null)
                            {
                                list5.Add(new PendingSymlink(scanDirectory, statEntry.Name, statEntry.LinkTarget));
                            }
                            break;
                        case StatCommandBuilder.StatEntryKind.Directory:
                            {
                                if (!CommandSyncEngine.IsIncluded(scanDirectory.RelativePath.Combine(statEntry.Name).Path, _settings.DirectoryFilters))
                                {
                                    break;
                                }
                                if (_visited.TryGetValue(key, out var value))
                                {
                                    List<RemoteFileSet.RemoteDirectory.RemoteSymlink> list3 = scanDirectory.RemoteDirectory.Symlinks;
                                    RemoteFileSet.RemoteDirectory.RemoteSymlink item = new RemoteFileSet.RemoteDirectory.RemoteSymlink
                                    {
                                        IsDirectory = true,
                                        Name = statEntry.Name
                                    };
                                    RelativeRemotePath relativeRemotePath = scanDirectory.RelativePath;
                                    string text3 = "/" + relativeRemotePath.ToString();
                                    relativeRemotePath = value.RelativePath;
                                    item.Target = RemotePathUtils.GetRelativePath(text3, "/" + relativeRemotePath.ToString(), flag: false);
                                    list3.Add(item);
                                }
                                else
                                {
                                    ScanDirectory scanDirectory2 = scanDirectory.CreateChild(statEntry.Name, statEntry.DeviceId);
                                    scanDirectory.RemoteDirectory.SubDirectories.Add(scanDirectory2.RemoteDirectory);
                                    if ((_settings.Options & SyncOptions.OneFileSystem) != SyncOptions.OneFileSystem || scanDirectory.DeviceId == 0 || statEntry.DeviceId == scanDirectory.DeviceId)
                                    {
                                        list.Add(scanDirectory2);
                                    }
                                }
                                break;
                            }
                    }
                }
            }
            return list;
        }
    }

    public const string FallbackScriptPrefix = "SysprogsSyncFallback-";

    protected readonly SshSession _session;

    protected readonly SyncSettings _settings;

    protected readonly ISyncEvents _events;

    protected readonly ILocalFileCache _cache;

    public readonly SyncTuning Tuning;

    protected readonly ISyncCustomization _customization;

    protected List<DownloadEntry> DownloadList { get; private set; }

    private bool IsUpload => _settings.Direction == SyncDirection.Upload;

    public override string ToString()
    {
        return "Command-based file synchronization engine";
    }

    protected CommandSyncEngine(SshSession sshSession, SyncSettings syncSettings, ISyncEvents syncEvents, ILocalFileCache localFileCache, SyncTuning syncTuning, ISyncCustomization syncCustomization)
    {
        _session = sshSession;
        _customization = syncCustomization;
        _settings = syncSettings;
        this._events = syncEvents;
        _cache = localFileCache;
        Tuning = syncTuning;
    }

    public static bool IsIncluded(string text, QuickSyncFilter[] items)
    {
        return QuickSyncRunner.IsIncluded(text, items);
    }

    public virtual void Dispose()
    {
    }

    protected List<string> RunCommand(string text2, bool flag = true)
    {
        _events?.OnLog(new SyncLogMessage.RunCommandMessage(text2));
        LineSplitter lineSplitter = new LineSplitter();
        try
        {
            IShellLauncher shellLauncher = null;
            if ((_settings.Options & SyncOptions.UseLauncher) != 0)
            {
                shellLauncher = _customization?.GetLauncher(text2);
            }
            using SshShellChannel sshShellChannel = _session.OpenShellChannel(shellLauncher?.Command ?? text2);
            shellLauncher?.Prepare(sshShellChannel, "File synchronization");
            sshShellChannel.DataReceived += (object obj, byte[] array, int num2, int num3, int num) =>
            {
                if (num == 1)
                {
                    foreach (string item in lineSplitter.Feed(array, num2, num3))
                    {
                        _events.OnWarning(new SyncWarning
                        {
                            Warning = new QuickSyncWarning(QuickSyncWarningType.StderrLine, item, 0)
                        });
                    }
                }
            };
            sshShellChannel.ReadStderrOnRead = true;
            List<string> list = new List<string>();
            using (StreamReader streamReader = new StreamReader(sshShellChannel.Output))
            {
                while (true)
                {
                    string text = streamReader.ReadLine();
                    if (text != null)
                    {
                        list.Add(text);
                        continue;
                    }
                    break;
                }
            }
            sshShellChannel.WaitClosed();
            if ((sshShellChannel.ExitStatus != 0) & flag)
            {
                return null;
            }
            return list;
        }
        catch (Exception ex)
        {
            _events.OnWarning(new SyncWarning
            {
                Warning = new QuickSyncWarning(QuickSyncWarningType.RemoteCommandFailed, text2, 0),
                Exception = ex
            });
            return null;
        }
    }

    public RemoteFileSet GetRemoteFileSet()
    {
        RemoteFileSet.RemoteDirectory remoteDirectory = new RemoteFileSet.RemoteDirectory(null, null);
        using (RemoteTreeScanner remoteTreeScanner = new RemoteTreeScanner(_settings.RemotePath, this))
        {
            remoteTreeScanner.Scan(remoteDirectory);
            DownloadList = remoteTreeScanner.Files;
        }
        return new RemoteFileSet
        {
            Root = remoteDirectory
        };
    }

    protected List<string> RunScript(string text2, byte[] buffer)
    {
        string text = _session.GetTempPath("SysprogsSyncFallback-" + text2);
        try
        {
            using (ScpChannelStream scpChannelStream = _session.OpenUpload(text, buffer.Length))
            {
                scpChannelStream.Write(buffer, 0, buffer.Length);
            }
            _events?.OnLog(new SyncLogMessage.RunScriptMessage(buffer));
            return RunCommand("sh \"" + text + "\"", flag: false) ?? new List<string>();
        }
        finally
        {
            if (!Tuning.KeepTempScripts)
            {
                RunCommand("rm \"" + text + "\"");
            }
        }
    }

    public abstract IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> items);

    public abstract void Upload(List<SyncPlan.SyncAction> list, List<LocalPath> list2, ref TransferStatistics transferStatistics);
}
