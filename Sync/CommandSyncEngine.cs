using SmarTTY.Ssh;
using SmarTTY.Ssh.QuickSync;
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

            public ScanDirectory(RemoteFileSet.RemoteDirectory remoteDirectory, RelativeRemotePath relativeRemotePath, string fullPath, int deviceId = 0)
            {
                this.RemoteDirectory = remoteDirectory;
                this.DeviceId = deviceId;
                RelativePath = relativeRemotePath;
                FullPath = fullPath;
            }

            public ScanDirectory CreateChild(string childName, int deviceId, string fullPath = null)
            {
                return new ScanDirectory(new RemoteFileSet.RemoteDirectory(this.RemoteDirectory, childName), RelativePath.Combine(childName), fullPath ?? (FullPath.TrimEnd('/') + "/" + childName), deviceId);
            }
        }

        private struct PendingSymlink
        {
            public ScanDirectory Parent;

            public string Name;

            public string Target;

            public string ResolvedPath => RemotePathUtils.NormalizeUnix(RemotePathUtils.Combine(Parent.FullPath, Target));

            public PendingSymlink(ScanDirectory scanDirectory, string name, string target)
            {
                Parent = scanDirectory;
                this.Name = name;
                this.Target = target;
            }
        }

        private SshSession _session;

        private readonly CommandSyncEngine _engine;

        private string _rootPath;

        private readonly SyncSettings _settings;

        private Dictionary<string, ScanDirectory> _visited = new Dictionary<string, ScanDirectory>();

        private StatCommandBuilder.StatFlavor _flavor;

        public List<DownloadEntry> Files = new List<DownloadEntry>();

        public RemoteTreeScanner(string rootPath, CommandSyncEngine commandSyncEngine)
        {
            this._session = commandSyncEngine._session;
            _engine = commandSyncEngine;
            this._rootPath = rootPath;
            _settings = commandSyncEngine._settings;
        }

        public void Dispose()
        {
        }

        private List<string> RunCommand(string command, bool requireSuccess = true)
        {
            return _engine.RunCommand(command, requireSuccess);
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
                    _flavor = StatCommandBuilder.DetectFlavor((string cmd) =>
                    {
                        _engine._events?.OnLog(new SyncLogMessage.RunCommandMessage(cmd));
                        return _session.RunCommandChecked(cmd, requireSuccess: true);
                    });
                }
                catch
                {
                    _flavor = StatCommandBuilder.StatFlavor.Unknown;
                }
            }
            StatCommandBuilder statCommandBuilder = new StatCommandBuilder(_flavor);
            string statCmd = statCommandBuilder.GetStatCommand(this._rootPath, followSymlinks: true);
            StatCommandBuilder.StatEntry rootEntry = RunCommand(statCmd)?.Select((string line) => statCommandBuilder.ParseEntry(line)).FirstOrDefault((StatCommandBuilder.StatEntry statEntry) => statEntry.Name != null) ?? default(StatCommandBuilder.StatEntry);
            List<ScanDirectory> pendingDirs = new List<ScanDirectory>();
            List<PendingSymlink> pendingLinks = new List<PendingSymlink>();
            pendingDirs.Add(new ScanDirectory(remoteDirectory, new RelativeRemotePath(""), this._rootPath, rootEntry.DeviceId));
            while (pendingDirs.Count > 0 || pendingLinks.Count > 0)
            {
                pendingDirs = ScanDirectories(statCommandBuilder, pendingDirs, pendingLinks);
                _engine._events?.OnProgress(new SyncProgress
                {
                    Stage = SyncStage.BuildingFileList
                });
                if (pendingDirs.Count == 0)
                {
                    ResolveSymlinks(pendingLinks, statCommandBuilder, pendingDirs);
                    pendingLinks.Clear();
                }
            }
        }

        private void ResolveSymlinks(List<PendingSymlink> pendingLinks, StatCommandBuilder statCommandBuilder, List<ScanDirectory> outPendingDirs)
        {
            string marker = Guid.NewGuid().ToString();
            MemoryStream memoryStream = new MemoryStream();
            using StreamWriter streamWriter = new StreamWriter(memoryStream)
            {
                NewLine = "\n"
            };
            streamWriter.WriteLine("echo " + marker);
            for (int i = 0; i < pendingLinks.Count; i++)
            {
                streamWriter.WriteLine($"echo ---link#{i}");
                streamWriter.WriteLine(statCommandBuilder.GetStatCommand(pendingLinks[i].ResolvedPath, followSymlinks: true) + $" || echo badlink:{i}");
            }
            streamWriter.Flush();
            List<string> scriptOutput = this._engine.RunScript(marker, memoryStream.ToArray());
            bool markerSeen = false;
            int currentLinkIndex = -1;
            foreach (string line in scriptOutput)
            {
                if (!markerSeen)
                {
                    if (!(line != marker))
                    {
                        markerSeen = true;
                    }
                }
                else if (line.StartsWith("---link#"))
                {
                    if (!int.TryParse(line.Substring(8), out currentLinkIndex))
                    {
                        currentLinkIndex = -1;
                    }
                }
                else
                {
                    if (currentLinkIndex < 0 || currentLinkIndex >= pendingLinks.Count)
                    {
                        continue;
                    }
                    if (line.StartsWith("badlink:") && int.TryParse(line.Substring(8), out var badIndex) && badIndex == currentLinkIndex)
                    {
                        this._engine._events?.OnWarning(new SyncWarning
                        {
                            Warning = new QuickSyncWarning(QuickSyncWarningType.UnreadableSourceSymlink, pendingLinks[badIndex].Parent.RelativePath.Combine(pendingLinks[badIndex].Name).Path, 0)
                        });
                        continue;
                    }
                    PendingSymlink pendingSymlink = pendingLinks[currentLinkIndex];
                    StatCommandBuilder.StatEntry statEntry = statCommandBuilder.ParseEntry(line);
                    statEntry.ModifiedTime -= _settings.ClockOffset.Seconds;
                    if (statEntry.Kind == StatCommandBuilder.StatEntryKind.Directory)
                    {
                        if (CommandSyncEngine.IsIncluded(pendingSymlink.Parent.RelativePath.Combine(pendingSymlink.Name).Path, _settings.DirectoryFilters))
                        {
                            ScanDirectory visitedDir;
                            if (this._engine.IsUpload)
                            {
                                pendingSymlink.Parent.RemoteDirectory.Symlinks.Add(new RemoteFileSet.RemoteDirectory.RemoteSymlink
                                {
                                    IsDirectory = true,
                                    Name = pendingSymlink.Name,
                                    Target = pendingSymlink.Target
                                });
                            }
                            else if (_visited.TryGetValue(pendingSymlink.ResolvedPath, out visitedDir))
                            {
                                List<RemoteFileSet.RemoteDirectory.RemoteSymlink> symlinksList = pendingSymlink.Parent.RemoteDirectory.Symlinks;
                                RemoteFileSet.RemoteDirectory.RemoteSymlink symlinkItem = new RemoteFileSet.RemoteDirectory.RemoteSymlink
                                {
                                    IsDirectory = true,
                                    Name = pendingSymlink.Name
                                };
                                RelativeRemotePath parentRelPath = pendingSymlink.Parent.RelativePath;
                                string currPath = "/" + parentRelPath.ToString();
                                parentRelPath = visitedDir.RelativePath;
                                symlinkItem.Target = RemotePathUtils.GetRelativePath(currPath, "/" + parentRelPath.ToString(), allowAbsoluteFallback: false);
                                symlinksList.Add(symlinkItem);
                            }
                            else
                            {
                                ScanDirectory scanDirectory = pendingSymlink.Parent.CreateChild(pendingSymlink.Name, statEntry.DeviceId, pendingSymlink.ResolvedPath);
                                pendingSymlink.Parent.RemoteDirectory.SubDirectories.Add(scanDirectory.RemoteDirectory);
                                outPendingDirs.Add(scanDirectory);
                            }
                        }
                    }
                    else if (statEntry.Kind == StatCommandBuilder.StatEntryKind.File && pendingSymlink.Parent.IsIncluded && CommandSyncEngine.IsIncluded(RemotePathUtils.GetFileName(pendingSymlink.Name), _settings.FileFilters))
                    {
                        string resolvedPath = pendingSymlink.ResolvedPath;
                        ScanDirectory parentVisited;
                        if (this._engine.IsUpload)
                        {
                            pendingSymlink.Parent.RemoteDirectory.Symlinks.Add(new RemoteFileSet.RemoteDirectory.RemoteSymlink
                            {
                                IsDirectory = false,
                                Name = pendingSymlink.Name,
                                Target = pendingSymlink.Target
                            });
                        }
                        else if (_visited.TryGetValue(RemotePathUtils.GetDirectoryName(resolvedPath), out parentVisited) && parentVisited.IsIncluded && CommandSyncEngine.IsIncluded(RemotePathUtils.GetFileName(pendingSymlink.Target), _settings.FileFilters))
                        {
                            List<RemoteFileSet.RemoteDirectory.RemoteSymlink> symlinksList = pendingSymlink.Parent.RemoteDirectory.Symlinks;
                            RemoteFileSet.RemoteDirectory.RemoteSymlink symlinkItem = new RemoteFileSet.RemoteDirectory.RemoteSymlink
                            {
                                IsDirectory = false,
                                Name = pendingSymlink.Name
                            };
                            RelativeRemotePath parentRelPath = pendingSymlink.Parent.RelativePath;
                            string currPath = "/" + parentRelPath.ToString();
                            parentRelPath = parentVisited.RelativePath;
                            symlinkItem.Target = RemotePathUtils.GetRelativePath(currPath, "/" + parentRelPath.ToString() + "/" + RemotePathUtils.GetFileName(pendingSymlink.Target), allowAbsoluteFallback: false);
                            symlinksList.Add(symlinkItem);
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
                                RemotePath = resolvedPath,
                                ModifiedSeconds = statEntry.ModifiedSeconds
                            });
                        }
                    }
                }
            }
        }

        private List<ScanDirectory> ScanDirectories(StatCommandBuilder statCommandBuilder, List<ScanDirectory> dirsToScan, List<PendingSymlink> pendingLinks)
        {
            List<ScanDirectory> nextDirsToScan = new List<ScanDirectory>();
            string marker = Guid.NewGuid().ToString();
            MemoryStream memoryStream = new MemoryStream();
            using StreamWriter streamWriter = new StreamWriter(memoryStream)
            {
                NewLine = "\n"
            };
            streamWriter.WriteLine("echo " + marker);
            foreach (ScanDirectory dir in dirsToScan)
            {
                string dirPath = dir.FullPath;
                _visited[dirPath] = dir;
                streamWriter.WriteLine("echo --dir:" + dirPath);
                streamWriter.WriteLine(statCommandBuilder.GetListCommand(dirPath));
            }
            streamWriter.Flush();
            List<string> scriptOutput = this._engine.RunScript(marker, memoryStream.ToArray());
            bool markerSeen = false;
            ScanDirectory currentDir = null;
            foreach (string line in scriptOutput)
            {
                if (!markerSeen)
                {
                    if (!(line != marker))
                    {
                        markerSeen = true;
                    }
                }
                else if (line.StartsWith("--dir:"))
                {
                    currentDir = _visited.GetOrDefault(line.Substring(6).Trim());
                    if (currentDir != null)
                    {
                        currentDir.IsIncluded = CommandSyncEngine.IsIncluded((currentDir.RelativePath.Path + "/.").TrimStart('/'), _settings.DirectoryFilters);
                    }
                }
                else
                {
                    if (currentDir == null)
                    {
                        continue;
                    }
                    StatCommandBuilder.StatEntry statEntry = statCommandBuilder.ParseEntry(line);
                    if (statEntry.Name == "." || statEntry.Name == "..")
                    {
                        continue;
                    }
                    string entryPath = currentDir.FullPath + "/" + statEntry.Name;
                    switch (statEntry.Kind)
                    {
                        case StatCommandBuilder.StatEntryKind.File:
                            if (currentDir.IsIncluded && CommandSyncEngine.IsIncluded(statEntry.Name, _settings.FileFilters))
                            {
                                currentDir.RemoteDirectory.Files.Add(new RemoteFileSet.RemoteFileEntry
                                {
                                    Id = Files.Count,
                                    Name = statEntry.Name,
                                    Size = statEntry.Size,
                                    ModifiedSeconds = statEntry.ModifiedSeconds
                                });
                                Files.Add(new DownloadEntry
                                {
                                    RemotePath = entryPath,
                                    ModifiedSeconds = statEntry.ModifiedSeconds
                                });
                            }
                            break;
                        case StatCommandBuilder.StatEntryKind.Symlink:
                            if (statEntry.LinkTarget != null)
                            {
                                pendingLinks.Add(new PendingSymlink(currentDir, statEntry.Name, statEntry.LinkTarget));
                            }
                            break;
                        case StatCommandBuilder.StatEntryKind.Directory:
                            {
                                if (!CommandSyncEngine.IsIncluded(currentDir.RelativePath.Combine(statEntry.Name).Path, _settings.DirectoryFilters))
                                {
                                    break;
                                }
                                if (_visited.TryGetValue(entryPath, out var visitedChild))
                                {
                                    List<RemoteFileSet.RemoteDirectory.RemoteSymlink> symlinksList = currentDir.RemoteDirectory.Symlinks;
                                    RemoteFileSet.RemoteDirectory.RemoteSymlink symlinkItem = new RemoteFileSet.RemoteDirectory.RemoteSymlink
                                    {
                                        IsDirectory = true,
                                        Name = statEntry.Name
                                    };
                                    RelativeRemotePath relPath = currentDir.RelativePath;
                                    string currPath = "/" + relPath.ToString();
                                    relPath = visitedChild.RelativePath;
                                    symlinkItem.Target = RemotePathUtils.GetRelativePath(currPath, "/" + relPath.ToString(), allowAbsoluteFallback: false);
                                    symlinksList.Add(symlinkItem);
                                }
                                else
                                {
                                    ScanDirectory childDir = currentDir.CreateChild(statEntry.Name, statEntry.DeviceId);
                                    currentDir.RemoteDirectory.SubDirectories.Add(childDir.RemoteDirectory);
                                    if ((_settings.Options & SyncOptions.OneFileSystem) != SyncOptions.OneFileSystem || currentDir.DeviceId == 0 || statEntry.DeviceId == currentDir.DeviceId)
                                    {
                                        nextDirsToScan.Add(childDir);
                                    }
                                }
                                break;
                            }
                    }
                }
            }
            return nextDirsToScan;
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

    public static bool IsIncluded(string path, QuickSyncFilter[] filters)
    {
        return QuickSyncRunner.IsIncluded(path, filters);
    }

    public virtual void Dispose()
    {
    }

    protected List<string> RunCommand(string command, bool requireSuccess = true)
    {
        _events?.OnLog(new SyncLogMessage.RunCommandMessage(command));
        LineSplitter lineSplitter = new LineSplitter();
        try
        {
            IShellLauncher shellLauncher = null;
            if ((_settings.Options & SyncOptions.UseLauncher) != 0)
            {
                shellLauncher = _customization?.GetLauncher(command);
            }
            using SshShellChannel sshShellChannel = _session.OpenShellChannel(shellLauncher?.Command ?? command);
            shellLauncher?.Prepare(sshShellChannel, "File synchronization");
            sshShellChannel.DataReceived += (object sender, byte[] buffer, int offset, int count, int dataType) =>
            {
                if (dataType == 1)
                {
                    foreach (string line in lineSplitter.Feed(buffer, offset, count))
                    {
                        _events.OnWarning(new SyncWarning
                        {
                            Warning = new QuickSyncWarning(QuickSyncWarningType.StderrLine, line, 0)
                        });
                    }
                }
            };
            sshShellChannel.ReadStderrOnRead = true;
            List<string> lines = new List<string>();
            using (StreamReader streamReader = new StreamReader(sshShellChannel.Output))
            {
                while (true)
                {
                    string line = streamReader.ReadLine();
                    if (line != null)
                    {
                        lines.Add(line);
                        continue;
                    }
                    break;
                }
            }
            sshShellChannel.WaitClosed();
            if ((sshShellChannel.ExitStatus != 0) & requireSuccess)
            {
                return null;
            }
            return lines;
        }
        catch (Exception exception)
        {
            _events.OnWarning(new SyncWarning
            {
                Warning = new QuickSyncWarning(QuickSyncWarningType.RemoteCommandFailed, command, 0),
                Exception = exception
            });
            return null;
        }
    }

    public RemoteFileSet GetRemoteFileSet()
    {
        RemoteFileSet.RemoteDirectory rootDir = new RemoteFileSet.RemoteDirectory(null, null);
        using (RemoteTreeScanner remoteTreeScanner = new RemoteTreeScanner(_settings.RemotePath, this))
        {
            remoteTreeScanner.Scan(rootDir);
            DownloadList = remoteTreeScanner.Files;
        }
        return new RemoteFileSet
        {
            Root = rootDir
        };
    }

    protected List<string> RunScript(string scriptName, byte[] buffer)
    {
        string remoteTempPath = _session.GetTempPath("SysprogsSyncFallback-" + scriptName);
        try
        {
            using (ScpChannelStream scpChannelStream = _session.OpenUpload(remoteTempPath, buffer.Length))
            {
                scpChannelStream.Write(buffer, 0, buffer.Length);
            }
            _events?.OnLog(new SyncLogMessage.RunScriptMessage(buffer));
            return RunCommand("sh \"" + remoteTempPath + "\"", requireSuccess: false) ?? new List<string>();
        }
        finally
        {
            if (!Tuning.KeepTempScripts)
            {
                RunCommand("rm \"" + remoteTempPath + "\"");
            }
        }
    }

    public abstract IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> actions);

    public abstract void Upload(List<SyncPlan.SyncAction> actions, List<LocalPath> localPaths, ref TransferStatistics transferStatistics);
}
