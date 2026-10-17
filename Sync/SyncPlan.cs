using SmarTTY.Ssh.QuickSync;
using SmarTTY.IO;
using SmarTTY.Native;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmarTTY.Sync;

internal class SyncPlan
{
    public enum ActionType
    {
        CreateAndEnterDirectory,
        EnterDirectory,
        GoToParentDirectory,
        TransferFile,
        DeleteTargetFile,
        DeleteEmptyTargetDirectory,
        DeleteTargetDirectoryRecursively,
        CreateRemoteSymlink,
        PrintStderrMessage
    }

    public struct SyncAction(ActionType actionType, string path, int argument = 0)
    {
        public readonly ActionType Type = actionType;

        public readonly string Path = path;

        public readonly int Argument = argument;

        public override string ToString()
        {
            return $"{Type} {Path}";
        }
    }

    public struct SymlinkRequest(LocalPath localPath, string target, bool isDirectory)
    {
        public readonly string LinkPath = localPath.FullPath;

        public readonly string Target = target;

        public readonly bool IsDirectory = isDirectory;
    }

    public class SyncPlannerBase
    {
        public TransferStatistics Statistics;

        public event Action<QuickSyncWarning, Exception> Warning;

        protected Dictionary<string, LocalFileSystem.FindData> ListLocalDirectory(LocalPath localPath)
        {
            Dictionary<string, LocalFileSystem.FindData> result = new Dictionary<string, LocalFileSystem.FindData>(StringComparer.InvariantCultureIgnoreCase);
            try
            {
                foreach (LocalFileSystem.FindData item in LocalFileSystem.EnumerateDirectory(localPath, ignoreMissing: true))
                {
                    result[item.FileName] = item;
                }
            }
            catch (Exception ex)
            {
                Warning?.Invoke(new QuickSyncWarning
                {
                    Path = localPath.FullPath,
                    Type = QuickSyncWarningType.UnreadableLocalDirectory
                }, ex);
            }
            return result;
        }

        protected void ReportWarning(QuickSyncWarning quickSyncWarning, Exception exception)
        {
            Warning?.Invoke(quickSyncWarning, exception);
        }

        protected bool IsIncluded(string path, QuickSyncFilter[] filters)
        {
            return QuickSyncRunner.IsIncluded(path, filters);
        }
    }

    public class DownloadPlanner : SyncPlannerBase
    {
        private class NameDeduplicator
        {
            private HashSet<string> _names = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            public bool TryAdd(string name)
            {
                if (_names.Contains(name))
                {
                    return false;
                }
                _names.Add(name);
                return true;
            }
        }

        private SyncPlan _plan;

        private SyncSettings _settings;

        private RemoteFileSet _remoteFileSet;

        public DownloadPlanner(SyncSettings syncSettings, RemoteFileSet remoteFileSet)
        {
            _settings = syncSettings;
            this._remoteFileSet = remoteFileSet;
        }

        public SyncPlan BuildPlan()
        {
            _plan = new SyncPlan();
            PlanDirectory(LocalPath.FromUserPath(_settings.LocalPath), _remoteFileSet.Root);
            return _plan;
        }

        private void PlanDirectory(LocalPath localPath, RemoteFileSet.RemoteDirectory remoteDirectory)
        {
            Dictionary<string, LocalFileSystem.FindData> localEntries = ListLocalDirectory(localPath);
            NameDeduplicator nameDeduplicator = new NameDeduplicator();
            base.Statistics.Directories++;
            foreach (RemoteFileSet.RemoteFileEntry fileEntry in remoteDirectory.Files)
            {
                if (fileEntry.Name.Contains(":"))
                {
                    base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.SkippingUnsupportedPath, localPath.Combine(fileEntry.Name).FullPath, 0), null);
                }
                else
                {
                    if (!IsIncluded(fileEntry.Name, _settings.FileFilters) || !nameDeduplicator.TryAdd(fileEntry.Name))
                    {
                        continue;
                    }
                    if (localEntries.TryGetValue(fileEntry.Name, out var findData))
                    {
                        localEntries.Remove(fileEntry.Name);
                        if (findData.HasAttribute(FileAttributes.Directory | FileAttributes.ReparsePoint))
                        {
                            PlanDelete(localPath, findData);
                        }
                        else
                        {
                            switch (_settings.QuickSyncOverwriteTrigger)
                            {
                                case QuickSyncOverwriteTrigger.OnlyIfMissing:
                                    base.Statistics.UpToDateFiles++;
                                    base.Statistics.UpToDateBytes += findData.Size;
                                    continue;
                                case QuickSyncOverwriteTrigger.Stat:
                                    if (LocalFileSystem.FileStamp.IsSame(fileEntry.GetFileInfo(), new LocalFileSystem.FileStamp(findData)))
                                    {
                                        base.Statistics.UpToDateFiles++;
                                        base.Statistics.UpToDateBytes += findData.Size;
                                        continue;
                                    }
                                    break;
                            }
                        }
                    }
                    base.Statistics.TotalBytes += fileEntry.Size;
                    this._plan.AddAction(ActionType.TransferFile, fileEntry.Name, fileEntry.Id);
                    base.Statistics.TotalFiles++;
                }
            }
            foreach (RemoteFileSet.RemoteDirectory dirEntry in remoteDirectory.SubDirectories)
            {
                if (dirEntry.Name.Contains(":"))
                {
                    base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.SkippingUnsupportedPath, localPath.Combine(dirEntry.Name).FullPath, 0), null);
                }
                else
                {
                    if ((dirEntry.FullPath != null && !IsIncluded(dirEntry.FullPath.Replace('\\', '/'), _settings.DirectoryFilters)) || !nameDeduplicator.TryAdd(dirEntry.Name))
                    {
                        continue;
                    }
                    bool exists = false;
                    if (localEntries.TryGetValue(dirEntry.Name, out var findData))
                    {
                        localEntries.Remove(dirEntry.Name);
                        if (findData.HasAttribute(FileAttributes.ReparsePoint) || !findData.HasAttribute(FileAttributes.Directory))
                        {
                            PlanDelete(localPath, findData);
                        }
                        else
                        {
                            exists = true;
                        }
                    }
                    using (this._plan.EnterDirectory(dirEntry.Name, exists, 0))
                    {
                        PlanDirectory(localPath.Combine(dirEntry.Name), dirEntry);
                    }
                }
            }
            foreach (RemoteFileSet.RemoteDirectory.RemoteSymlink symlinkEntry in remoteDirectory.Symlinks)
            {
                if (symlinkEntry.Name.Contains(":"))
                {
                    base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.SkippingUnsupportedPath, localPath.Combine(symlinkEntry.Name).FullPath, 0), null);
                }
                else
                {
                    if (!nameDeduplicator.TryAdd(symlinkEntry.Name))
                    {
                        continue;
                    }
                    base.Statistics.Symlinks++;
                    if (symlinkEntry.IsDirectory)
                    {
                        if (!IsIncluded(remoteDirectory.Combine(symlinkEntry.Name).Replace('\\', '/'), _settings.DirectoryFilters))
                        {
                            continue;
                        }
                    }
                    else if (!IsIncluded(symlinkEntry.Name, _settings.FileFilters))
                    {
                        continue;
                    }
                    if (localEntries.TryGetValue(symlinkEntry.Name, out var findData))
                    {
                        localEntries.Remove(symlinkEntry.Name);
                        if (findData.HasAttribute(FileAttributes.ReparsePoint) && findData.HasAttribute(FileAttributes.Directory) == symlinkEntry.IsDirectory)
                        {
                            try
                            {
                                KeyValuePair<string, bool> linkInfo = ReparsePoints.ReadReparsePoint(localPath.Combine(symlinkEntry.Name).FullPath);
                                if (linkInfo.Key == symlinkEntry.Target && linkInfo.Value)
                                {
                                    continue;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    this._plan.Symlinks.Add(new SymlinkRequest(localPath.Combine(symlinkEntry.Name), symlinkEntry.Target, symlinkEntry.IsDirectory));
                }
            }
            if ((_settings.Options & SyncOptions.DeleteExtraneous) == 0)
            {
                return;
            }
            foreach (KeyValuePair<string, LocalFileSystem.FindData> item in localEntries)
            {
                if (item.Value.HasAttribute(FileAttributes.Directory))
                {
                    if (!IsIncluded(remoteDirectory.Combine(item.Key).Replace('\\', '/'), _settings.DirectoryFilters))
                    {
                        continue;
                    }
                }
                else if (!IsIncluded(item.Key, _settings.FileFilters))
                {
                    continue;
                }
                PlanDelete(localPath, item.Value);
            }
        }

        public void PlanDelete(LocalPath localPath, LocalFileSystem.FindData findData)
        {
            LocalPath fullLocalPath = localPath.Combine(findData.FileName);
            if (findData.HasAttribute(FileAttributes.ReparsePoint))
            {
                if (findData.HasAttribute(FileAttributes.Directory))
                {
                    this._plan.AddAction(ActionType.DeleteEmptyTargetDirectory, fullLocalPath.FullPath);
                    return;
                }
                base.Statistics.FilesToDelete++;
                this._plan.AddAction(ActionType.DeleteTargetFile, fullLocalPath.FullPath);
                return;
            }
            if (findData.HasAttribute(FileAttributes.Directory))
            {
                try
                {
                    foreach (LocalFileSystem.FindData childData in LocalFileSystem.EnumerateDirectory(fullLocalPath, ignoreMissing: false))
                    {
                        PlanDelete(fullLocalPath, childData);
                    }
                    this._plan.AddAction(ActionType.DeleteEmptyTargetDirectory, fullLocalPath.FullPath);
                    return;
                }
                catch (Exception ex)
                {
                    base.ReportWarning(new QuickSyncWarning
                    {
                        Path = fullLocalPath.FullPath,
                        Type = QuickSyncWarningType.UnreadableLocalDirectory
                    }, ex);
                    return;
                }
            }
            base.Statistics.FilesToDelete++;
            this._plan.AddAction(ActionType.DeleteTargetFile, fullLocalPath.FullPath);
        }

        public SyncPlan PlanDeleteNames(LocalPath localPath, string[] names)
        {
            this._plan = new SyncPlan();
            HashSet<string> nameSet = DataExtensions.ToHashSet(names.EnsureNotNull(), StringComparer.InvariantCultureIgnoreCase);
            foreach (LocalFileSystem.FindData findData in LocalFileSystem.EnumerateDirectory(localPath, ignoreMissing: true))
            {
                if (findData.HasAttribute(FileAttributes.Directory) && nameSet.Contains(findData.FileName ?? ""))
                {
                    PlanDelete(localPath, findData);
                }
            }
            return this._plan;
        }
    }

    public class UploadPlanner : SyncPlannerBase
    {
        private struct VisitedDirectory(RelativeRemotePath relativeRemotePath, bool isIncluded)
        {
            public RelativeRemotePath RelativePath = relativeRemotePath;

            public bool IsIncluded = isIncluded;
        }

        private struct LocalEntry
        {
            public string Name;

            public FileAttributes Attributes;

            public LocalFileSystem.FileStamp Stamp;

            public LocalDirectoryListing Children;

            public bool HasAttribute(FileAttributes fileAttributes)
            {
                return (Attributes & fileAttributes) != 0;
            }
        }

        private struct LocalDirectoryListing
        {
            public LocalEntry[] Entries;

            public bool IsIncluded;
        }

        private enum RemoteEntryKind
        {
            None,
            File,
            Directory,
            Symlink
        }

        private struct RemoteEntryRef(RemoteEntryKind remoteEntryKind, int index)
        {
            public RemoteEntryKind Kind = remoteEntryKind;

            public int Index = index;
        }

        private SyncPlan _plan;

        private SyncSettings _settings;

        private RemoteFileSet _remoteFileSet;

        private readonly ILocalFileCache _cache;

        private readonly bool _remoteIsEmpty;

        private readonly ISyncCustomization _customization;

        private Dictionary<string, VisitedDirectory> _visited = new Dictionary<string, VisitedDirectory>(StringComparer.InvariantCultureIgnoreCase);

        public readonly List<LocalPath> Files = new List<LocalPath>();

        private RemoteFileSet.RemoteDirectory _emptyDirectory = new RemoteFileSet.RemoteDirectory(null, null);

        public UploadPlanner(SyncSettings syncSettings, RemoteFileSet remoteFileSet, ILocalFileCache localFileCache, bool remoteIsEmpty, ISyncCustomization syncCustomization)
        {
            _settings = syncSettings;
            _remoteFileSet = remoteFileSet;
            this._cache = localFileCache;
            _remoteIsEmpty = remoteIsEmpty;
            this._customization = syncCustomization;
            Files.Add(LocalPath.FromRaw(""));
        }

        private LocalDirectoryListing ListDirectory(LocalPath localPath, RelativeRemotePath relativeRemotePath)
        {
            bool isIncluded = IsIncluded(relativeRemotePath.Combine(".").Path, _settings.DirectoryFilters);
            _visited[localPath.FullPath] = new VisitedDirectory(relativeRemotePath, isIncluded);
            List<LocalEntry> entryList = new List<LocalEntry>();
            LocalFileSystem.FindData[] findDataArray;
            try
            {
                findDataArray = LocalFileSystem.EnumerateDirectory(localPath, ignoreMissing: true).ToArray();
            }
            catch (Exception ex)
            {
                base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.UnreadableLocalDirectory, localPath.FullPath, 0), ex);
                findDataArray = new LocalFileSystem.FindData[0];
            }
            foreach (LocalFileSystem.FindData findData in findDataArray)
            {
                if (findData.HasAttribute(FileAttributes.Directory))
                {
                    ISyncCustomization syncCustomization = _customization;
                    if ((syncCustomization == null || !syncCustomization.IsDirectoryIgnored(localPath, findData.FileName)) && IsIncluded(relativeRemotePath.Combine(findData.FileName).Path, _settings.DirectoryFilters))
                    {
                        entryList.Add(new LocalEntry
                        {
                            Name = findData.FileName,
                            Attributes = findData.Attributes,
                            Stamp = new LocalFileSystem.FileStamp(findData)
                        });
                    }
                }
                else if (isIncluded)
                {
                    ISyncCustomization syncCustomization2 = _customization;
                    if ((syncCustomization2 == null || !syncCustomization2.IsFileIgnored(localPath, findData.FileName)) && IsIncluded(findData.FileName, _settings.FileFilters))
                    {
                        entryList.Add(new LocalEntry
                        {
                            Name = findData.FileName,
                            Attributes = findData.Attributes,
                            Stamp = new LocalFileSystem.FileStamp(findData)
                        });
                    }
                }
            }
            return new LocalDirectoryListing
            {
                Entries = entryList.ToArray(),
                IsIncluded = isIncluded
            };
        }

        public SyncPlan BuildPlan()
        {
            _plan = new SyncPlan();
            LocalDirectoryListing localDirectoryListing = ListRecursive(LocalPath.FromUserPath(_settings.LocalPath), new RelativeRemotePath(""));
            PlanDirectory(LocalPath.FromUserPath(_settings.LocalPath), _remoteFileSet.Root, new RelativeRemotePath(""), localDirectoryListing);
            return _plan;
        }

        private LocalDirectoryListing ListRecursive(LocalPath localPath, RelativeRemotePath relativeRemotePath)
        {
            LocalDirectoryListing result = ListDirectory(localPath, relativeRemotePath);
            for (int i = 0; i < result.Entries.Length; i++)
            {
                if (result.Entries[i].HasAttribute(FileAttributes.Directory) && !result.Entries[i].HasAttribute(FileAttributes.ReparsePoint))
                {
                    result.Entries[i].Children = ListRecursive(localPath.Combine(result.Entries[i].Name), relativeRemotePath.Combine(result.Entries[i].Name));
                }
            }
            return result;
        }

        private int AddFile(LocalPath localPath, long size)
        {
            int count = Files.Count;
            Files.Add(localPath);
            base.Statistics.TotalBytes += size;
            base.Statistics.TotalFiles++;
            return count;
        }

        private void PlanDirectory(LocalPath localPath, RemoteFileSet.RemoteDirectory remoteDirectory, RelativeRemotePath relativeRemotePath, LocalDirectoryListing localDirectoryListing)
        {
            Dictionary<string, RemoteEntryRef> remoteEntries = IndexRemoteDirectory(remoteDirectory);
            ICachedDirectory cachedDirectory = _cache?.GetDirectory(localPath);
            if (localDirectoryListing.Entries == null)
            {
                localDirectoryListing = ListDirectory(localPath, relativeRemotePath);
            }
            foreach (LocalEntry localEntry in localDirectoryListing.Entries)
            {
                LocalPath childLocalPath = localPath.Combine(localEntry.Name);
                bool existsRemotely = remoteEntries.TryGetValue(localEntry.Name, out var remoteRef);
                if (existsRemotely)
                {
                    remoteEntries.Remove(localEntry.Name);
                }
                LocalFileSystem.FileStamp fileStamp = localEntry.Stamp;
                if (localEntry.HasAttribute(FileAttributes.ReparsePoint))
                {
                    try
                    {
                        childLocalPath = LocalFileSystem.ResolveFinalSymlink(childLocalPath);
                    }
                    catch (Exception ex)
                    {
                        base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.UnreadableSourceSymlink, childLocalPath.FullPath, 0), ex);
                        continue;
                    }
                    string symlinkTarget = null;
                    if (localEntry.HasAttribute(FileAttributes.Directory))
                    {
                        if (_visited.TryGetValue(childLocalPath.FullPath, out var visitedDir))
                        {
                            string currRelPath = "/" + relativeRemotePath.Path;
                            RelativeRemotePath targetRelPath = visitedDir.RelativePath;
                            symlinkTarget = RemotePathUtils.GetRelativePath(currRelPath, "/" + targetRelPath.ToString(), allowAbsoluteFallback: false);
                        }
                    }
                    else if (_visited.TryGetValue(childLocalPath.GetParent().FullPath, out var visitedParent) && visitedParent.IsIncluded && IsIncluded(childLocalPath.FileName, _settings.FileFilters))
                    {
                        string currRelPath = "/" + relativeRemotePath.Path;
                        RelativeRemotePath targetRelPath = visitedParent.RelativePath;
                        string parentRelPath = RemotePathUtils.GetRelativePath(currRelPath, "/" + targetRelPath.ToString(), allowAbsoluteFallback: false);
                        string fileName = childLocalPath.FileName;
                        symlinkTarget = ((!(parentRelPath == "") && !(parentRelPath == ".")) ? (parentRelPath + "/" + fileName) : fileName);
                    }
                    else
                    {
                        try
                        {
                            using LocalFileSystem.NativeFile nativeFile = LocalFileSystem.OpenFile(childLocalPath, LocalFileSystem.FileOpenMode.Open);
                            fileStamp = new LocalFileSystem.FileStamp(nativeFile.GetSize(), nativeFile.GetLastWriteTime());
                        }
                        catch (Exception ex)
                        {
                            base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.CannotStatLocalSymlink, childLocalPath.FullPath, 0), ex);
                        }
                    }
                    if (symlinkTarget != null)
                    {
                        if (!existsRemotely || remoteRef.Kind != SyncPlan.UploadPlanner.RemoteEntryKind.Symlink || !(remoteDirectory.Symlinks[remoteRef.Index].Target == symlinkTarget))
                        {
                            if (existsRemotely)
                            {
                                PlanDeleteEntry(remoteRef, remoteDirectory, localEntry.Name);
                            }
                            bool isUpToDate = cachedDirectory?.IsSymlinkUpToDate(localEntry.Name, symlinkTarget) ?? true;
                            if (_settings.QuickSyncOverwriteTrigger != QuickSyncOverwriteTrigger.LocalCache || cachedDirectory == null || _remoteIsEmpty || isUpToDate)
                            {
                                _plan.AddAction(ActionType.CreateRemoteSymlink, relativeRemotePath.Combine(localEntry.Name).ToString() + "\n" + symlinkTarget);
                            }
                        }
                        continue;
                    }
                }
                if (existsRemotely)
                {
                    if (localEntry.HasAttribute(FileAttributes.Directory))
                    {
                        if (remoteRef.Kind != SyncPlan.UploadPlanner.RemoteEntryKind.Directory)
                        {
                            PlanDeleteEntry(remoteRef, remoteDirectory, localEntry.Name);
                        }
                    }
                    else if (remoteRef.Kind != SyncPlan.UploadPlanner.RemoteEntryKind.File)
                    {
                        PlanDeleteEntry(remoteRef, remoteDirectory, localEntry.Name);
                    }
                    else
                    {
                        switch (_settings.QuickSyncOverwriteTrigger)
                        {
                            case QuickSyncOverwriteTrigger.OnlyIfMissing:
                                base.Statistics.UpToDateFiles++;
                                base.Statistics.UpToDateBytes += fileStamp.Size;
                                continue;
                            case QuickSyncOverwriteTrigger.Stat:
                                if (LocalFileSystem.FileStamp.IsSame(fileStamp, remoteDirectory.Files[remoteRef.Index].GetFileInfo()))
                                {
                                    base.Statistics.UpToDateFiles++;
                                    base.Statistics.UpToDateBytes += fileStamp.Size;
                                    continue;
                                }
                                break;
                        }
                    }
                }
                if (localEntry.HasAttribute(FileAttributes.Directory))
                {
                    RelativeRemotePath childRemotePath = relativeRemotePath.Combine(localEntry.Name);
                    SyncPlan syncPlan = _plan;
                    string dirName = localEntry.Name;
                    bool isExistingDir = existsRemotely && remoteRef.Kind == SyncPlan.UploadPlanner.RemoteEntryKind.Directory;
                    LocalFileSystem.FileStamp stamp = localEntry.Stamp;
                    using (syncPlan.EnterDirectory(dirName, isExistingDir, stamp.ModifiedSeconds))
                    {
                        RemoteFileSet.RemoteDirectory subRemoteDir = this._emptyDirectory;
                        if (existsRemotely && remoteRef.Kind == SyncPlan.UploadPlanner.RemoteEntryKind.Directory)
                        {
                            subRemoteDir = remoteDirectory.SubDirectories[remoteRef.Index];
                        }
                        PlanDirectory(childLocalPath, subRemoteDir, childRemotePath, localEntry.Children);
                    }
                }
                else
                {
                    bool isUpToDate = cachedDirectory?.IsFileUpToDate(localEntry.Name, fileStamp) ?? true;
                    if (_settings.QuickSyncOverwriteTrigger == QuickSyncOverwriteTrigger.LocalCache && cachedDirectory != null && !_remoteIsEmpty && !isUpToDate)
                    {
                        base.Statistics.UpToDateFiles++;
                        base.Statistics.UpToDateBytes += fileStamp.Size;
                    }
                    else
                    {
                        int fileId = AddFile(childLocalPath, fileStamp.Size);
                        cachedDirectory?.Register(fileId, localEntry.Name);
                        _plan.AddAction(ActionType.TransferFile, localEntry.Name, fileId);
                    }
                }
            }
            if ((_settings.Options & SyncOptions.DeleteExtraneous) != SyncOptions.DeleteExtraneous || !localDirectoryListing.IsIncluded)
            {
                return;
            }
            foreach (KeyValuePair<string, RemoteEntryRef> item in remoteEntries)
            {
                PlanDeleteEntry(item.Value, remoteDirectory, item.Key);
            }
            if (_settings.QuickSyncOverwriteTrigger != QuickSyncOverwriteTrigger.LocalCache || cachedDirectory == null || _remoteIsEmpty)
            {
                return;
            }
            foreach (string entryName in cachedDirectory.GetEntryNames().EnsureNotNull())
            {
                if (IsIncluded(entryName, _settings.FileFilters))
                {
                    string fullRemotePath = relativeRemotePath.PrependBase(_settings.RemotePath) + "/" + entryName;
                    base.Statistics.FilesToDelete++;
                    _plan.AddAction(ActionType.DeleteTargetFile, fullRemotePath);
                }
            }
        }

        private void DeleteDirectoryContents(RemoteFileSet.RemoteDirectory remoteDirectory, string dirName)
        {
            _plan.AddAction(ActionType.EnterDirectory, dirName);
            foreach (KeyValuePair<string, RemoteEntryRef> item in IndexRemoteDirectory(remoteDirectory))
            {
                PlanDeleteEntry(item.Value, remoteDirectory, item.Key);
            }
            _plan.AddAction(ActionType.GoToParentDirectory, null);
            _plan.AddAction(ActionType.DeleteEmptyTargetDirectory, dirName);
        }

        private void PlanDeleteEntry(RemoteEntryRef remoteEntryRef, RemoteFileSet.RemoteDirectory remoteDirectory, string entryName)
        {
            switch (remoteEntryRef.Kind)
            {
                case SyncPlan.UploadPlanner.RemoteEntryKind.File:
                case SyncPlan.UploadPlanner.RemoteEntryKind.Symlink:
                    _plan.AddAction(ActionType.DeleteTargetFile, entryName);
                    break;
                case SyncPlan.UploadPlanner.RemoteEntryKind.Directory:
                    if (_settings.FileFilters.IsNullOrEmpty() && _settings.DirectoryFilters.IsNullOrEmpty())
                    {
                        _plan.AddAction(ActionType.DeleteTargetDirectoryRecursively, entryName);
                    }
                    else
                    {
                        DeleteDirectoryContents(remoteDirectory.SubDirectories[remoteEntryRef.Index], entryName);
                    }
                    break;
            }
        }

        private static Dictionary<string, RemoteEntryRef> IndexRemoteDirectory(RemoteFileSet.RemoteDirectory remoteDirectory)
        {
            Dictionary<string, RemoteEntryRef> result = new Dictionary<string, RemoteEntryRef>();
            for (int i = 0; i < remoteDirectory.Files.Count; i++)
            {
                result[remoteDirectory.Files[i].Name] = new RemoteEntryRef(SyncPlan.UploadPlanner.RemoteEntryKind.File, i);
            }
            for (int i = 0; i < remoteDirectory.SubDirectories.Count; i++)
            {
                result[remoteDirectory.SubDirectories[i].Name] = new RemoteEntryRef(SyncPlan.UploadPlanner.RemoteEntryKind.Directory, i);
            }
            for (int i = 0; i < remoteDirectory.Symlinks.Count; i++)
            {
                result[remoteDirectory.Symlinks[i].Name] = new RemoteEntryRef(SyncPlan.UploadPlanner.RemoteEntryKind.Symlink, i);
            }
            return result;
        }
    }

    private class DirectoryScope : IDisposable
    {
        private SyncPlan _plan;

        private int _depth;

        public DirectoryScope(SyncPlan syncPlan, int depth)
        {
            this._plan = syncPlan;
            this._depth = depth;
        }

        public void Dispose()
        {
            if (_plan.Actions.Count == _depth)
            {
                _plan.Actions.RemoveAt(_depth - 1);
                return;
            }
            if (_plan.Actions.Count < _depth)
            {
                throw new Exception("SysprogsSync directory scope underflow");
            }
            _plan.AddAction(ActionType.GoToParentDirectory, null);
        }
    }

    public List<SyncAction> Actions = new List<SyncAction>();

    public List<SymlinkRequest> Symlinks = new List<SymlinkRequest>();

    private void AddAction(ActionType actionType, string path, int argument = 0)
    {
        Actions.Add(new SyncAction(actionType, path, argument));
    }

    public IDisposable EnterDirectory(string dirName, bool exists, int argument)
    {
        AddAction(exists ? ActionType.EnterDirectory : ActionType.CreateAndEnterDirectory, dirName, argument);
        int depth = Actions.Count;
        return new DirectoryScope(this, depth);
    }
}
