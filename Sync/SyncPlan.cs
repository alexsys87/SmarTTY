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

    public struct SyncAction(ActionType actionType, string text, int value2 = 0)
    {
        public readonly ActionType Type = actionType;

        public readonly string Path = text;

        public readonly int Argument = value2;

        public override string ToString()
        {
            return $"{Type} {Path}";
        }
    }

    public struct SymlinkRequest(LocalPath localPath, string text, bool flag)
    {
        public readonly string LinkPath = localPath.FullPath;

        public readonly string Target = text;

        public readonly bool IsDirectory = flag;
    }

    public class SyncPlannerBase
    {
        public TransferStatistics Statistics;

        public event Action<QuickSyncWarning, Exception> Warning;

        protected Dictionary<string, LocalFileSystem.FindData> ListLocalDirectory(LocalPath localPath)
        {
            Dictionary<string, LocalFileSystem.FindData> dictionary = new Dictionary<string, LocalFileSystem.FindData>(StringComparer.InvariantCultureIgnoreCase);
            try
            {
                foreach (LocalFileSystem.FindData item in LocalFileSystem.EnumerateDirectory(localPath, flag: true))
                {
                    dictionary[item.FileName] = item;
                }
            }
            catch (Exception arg)
            {
                Warning?.Invoke(new QuickSyncWarning
                {
                    Path = localPath.FullPath,
                    Type = QuickSyncWarningType.UnreadableLocalDirectory
                }, arg);
            }
            return dictionary;
        }

        protected void ReportWarning(QuickSyncWarning quickSyncWarning, Exception exception)
        {
            Warning?.Invoke(quickSyncWarning, exception);
        }

        protected bool IsIncluded(string text, QuickSyncFilter[] items)
        {
            return QuickSyncRunner.IsIncluded(text, items);
        }
    }

    public class DownloadPlanner : SyncPlannerBase
    {
        private class NameDeduplicator
        {
            private HashSet<string> _names = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            public bool TryAdd(string text)
            {
                if (_names.Contains(text))
                {
                    return false;
                }
                _names.Add(text);
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
            Dictionary<string, LocalFileSystem.FindData> dictionary = ListLocalDirectory(localPath);
            NameDeduplicator nameDeduplicator = new NameDeduplicator();
            base.Statistics.Directories++;
            foreach (RemoteFileSet.RemoteFileEntry item in remoteDirectory.Files)
            {
                if (item.Name.Contains(":"))
                {
                    base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.SkippingUnsupportedPath, localPath.Combine(item.Name).FullPath, 0), null);
                }
                else
                {
                    if (!IsIncluded(item.Name, _settings.FileFilters) || !nameDeduplicator.TryAdd(item.Name))
                    {
                        continue;
                    }
                    if (dictionary.TryGetValue(item.Name, out var value))
                    {
                        dictionary.Remove(item.Name);
                        if (value.HasAttribute(FileAttributes.Directory | FileAttributes.ReparsePoint))
                        {
                            PlanDelete(localPath, value);
                        }
                        else
                        {
                            switch (_settings.QuickSyncOverwriteTrigger)
                            {
                                case QuickSyncOverwriteTrigger.OnlyIfMissing:
                                    base.Statistics.UpToDateFiles++;
                                    base.Statistics.UpToDateBytes += value.Size;
                                    continue;
                                case QuickSyncOverwriteTrigger.Stat:
                                    if (LocalFileSystem.FileStamp.IsSame(item.GetFileInfo(), new LocalFileSystem.FileStamp(value)))
                                    {
                                        base.Statistics.UpToDateFiles++;
                                        base.Statistics.UpToDateBytes += value.Size;
                                        continue;
                                    }
                                    break;
                            }
                        }
                    }
                    base.Statistics.TotalBytes += item.Size;
                    this._plan.AddAction(ActionType.TransferFile, item.Name, item.Id);
                    base.Statistics.TotalFiles++;
                }
            }
            foreach (RemoteFileSet.RemoteDirectory item2 in remoteDirectory.SubDirectories)
            {
                if (item2.Name.Contains(":"))
                {
                    base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.SkippingUnsupportedPath, localPath.Combine(item2.Name).FullPath, 0), null);
                }
                else
                {
                    if ((item2.FullPath != null && !IsIncluded(item2.FullPath.Replace('\\', '/'), _settings.DirectoryFilters)) || !nameDeduplicator.TryAdd(item2.Name))
                    {
                        continue;
                    }
                    bool flag = false;
                    if (dictionary.TryGetValue(item2.Name, out var value2))
                    {
                        dictionary.Remove(item2.Name);
                        if (value2.HasAttribute(FileAttributes.ReparsePoint) || !value2.HasAttribute(FileAttributes.Directory))
                        {
                            PlanDelete(localPath, value2);
                        }
                        else
                        {
                            flag = true;
                        }
                    }
                    using (this._plan.EnterDirectory(item2.Name, flag, 0))
                    {
                        PlanDirectory(localPath.Combine(item2.Name), item2);
                    }
                }
            }
            foreach (RemoteFileSet.RemoteDirectory.RemoteSymlink item3 in remoteDirectory.Symlinks)
            {
                if (item3.Name.Contains(":"))
                {
                    base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.SkippingUnsupportedPath, localPath.Combine(item3.Name).FullPath, 0), null);
                }
                else
                {
                    if (!nameDeduplicator.TryAdd(item3.Name))
                    {
                        continue;
                    }
                    base.Statistics.Symlinks++;
                    if (item3.IsDirectory)
                    {
                        if (!IsIncluded(remoteDirectory.Combine(item3.Name).Replace('\\', '/'), _settings.DirectoryFilters))
                        {
                            continue;
                        }
                    }
                    else if (!IsIncluded(item3.Name, _settings.FileFilters))
                    {
                        continue;
                    }
                    if (dictionary.TryGetValue(item3.Name, out var value3))
                    {
                        dictionary.Remove(item3.Name);
                        if (value3.HasAttribute(FileAttributes.ReparsePoint) && value3.HasAttribute(FileAttributes.Directory) == item3.IsDirectory)
                        {
                            try
                            {
                                KeyValuePair<string, bool> keyValuePair = ReparsePoints.ReadReparsePoint(localPath.Combine(item3.Name).FullPath);
                                if (keyValuePair.Key == item3.Target && keyValuePair.Value)
                                {
                                    continue;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    this._plan.Symlinks.Add(new SymlinkRequest(localPath.Combine(item3.Name), item3.Target, item3.IsDirectory));
                }
            }
            if ((_settings.Options & SyncOptions.DeleteExtraneous) == 0)
            {
                return;
            }
            foreach (KeyValuePair<string, LocalFileSystem.FindData> item4 in dictionary)
            {
                if (item4.Value.HasAttribute(FileAttributes.Directory))
                {
                    if (!IsIncluded(remoteDirectory.Combine(item4.Key).Replace('\\', '/'), _settings.DirectoryFilters))
                    {
                        continue;
                    }
                }
                else if (!IsIncluded(item4.Key, _settings.FileFilters))
                {
                    continue;
                }
                PlanDelete(localPath, item4.Value);
            }
        }

        public void PlanDelete(LocalPath localPath, LocalFileSystem.FindData findData)
        {
            LocalPath localPath2 = localPath.Combine(findData.FileName);
            if (findData.HasAttribute(FileAttributes.ReparsePoint))
            {
                if (findData.HasAttribute(FileAttributes.Directory))
                {
                    this._plan.AddAction(ActionType.DeleteEmptyTargetDirectory, localPath2.FullPath);
                    return;
                }
                base.Statistics.FilesToDelete++;
                this._plan.AddAction(ActionType.DeleteTargetFile, localPath2.FullPath);
                return;
            }
            if (findData.HasAttribute(FileAttributes.Directory))
            {
                try
                {
                    foreach (LocalFileSystem.FindData item in LocalFileSystem.EnumerateDirectory(localPath2, flag: false))
                    {
                        PlanDelete(localPath2, item);
                    }
                    this._plan.AddAction(ActionType.DeleteEmptyTargetDirectory, localPath2.FullPath);
                    return;
                }
                catch (Exception ex)
                {
                    base.ReportWarning(new QuickSyncWarning
                    {
                        Path = localPath2.FullPath,
                        Type = QuickSyncWarningType.UnreadableLocalDirectory
                    }, ex);
                    return;
                }
            }
            base.Statistics.FilesToDelete++;
            this._plan.AddAction(ActionType.DeleteTargetFile, localPath2.FullPath);
        }

        public SyncPlan PlanDeleteNames(LocalPath localPath, string[] items)
        {
            this._plan = new SyncPlan();
            HashSet<string> hashSet = DataExtensions.ToHashSet(items.EnsureNotNull(), StringComparer.InvariantCultureIgnoreCase);
            foreach (LocalFileSystem.FindData item in LocalFileSystem.EnumerateDirectory(localPath, flag: true))
            {
                if (item.HasAttribute(FileAttributes.Directory) && hashSet.Contains(item.FileName ?? ""))
                {
                    PlanDelete(localPath, item);
                }
            }
            return this._plan;
        }
    }

    public class UploadPlanner : SyncPlannerBase
    {
        private struct VisitedDirectory(RelativeRemotePath relativeRemotePath, bool flag)
        {
            public RelativeRemotePath RelativePath = relativeRemotePath;

            public bool IsIncluded = flag;
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

        private struct RemoteEntryRef(RemoteEntryKind remoteEntryKind, int value2)
        {
            public RemoteEntryKind Kind = remoteEntryKind;

            public int Index = value2;
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

        public UploadPlanner(SyncSettings syncSettings, RemoteFileSet remoteFileSet, ILocalFileCache localFileCache, bool flag, ISyncCustomization syncCustomization)
        {
            _settings = syncSettings;
            _remoteFileSet = remoteFileSet;
            this._cache = localFileCache;
            _remoteIsEmpty = flag;
            this._customization = syncCustomization;
            Files.Add(LocalPath.FromRaw(""));
        }

        private LocalDirectoryListing ListDirectory(LocalPath localPath, RelativeRemotePath relativeRemotePath)
        {
            bool flag = IsIncluded(relativeRemotePath.Combine(".").Path, _settings.DirectoryFilters);
            _visited[localPath.FullPath] = new VisitedDirectory(relativeRemotePath, flag);
            List<LocalEntry> list = new List<LocalEntry>();
            LocalFileSystem.FindData[] array;
            try
            {
                array = LocalFileSystem.EnumerateDirectory(localPath, flag: true).ToArray();
            }
            catch (Exception ex)
            {
                base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.UnreadableLocalDirectory, localPath.FullPath, 0), ex);
                array = new LocalFileSystem.FindData[0];
            }
            LocalFileSystem.FindData[] array2 = array;
            for (int num = 0; num < array2.Length; num++)
            {
                LocalFileSystem.FindData findData = array2[num];
                if (findData.HasAttribute(FileAttributes.Directory))
                {
                    ISyncCustomization syncCustomization = _customization;
                    if ((syncCustomization == null || !syncCustomization.IsDirectoryIgnored(localPath, findData.FileName)) && IsIncluded(relativeRemotePath.Combine(findData.FileName).Path, _settings.DirectoryFilters))
                    {
                        list.Add(new LocalEntry
                        {
                            Name = findData.FileName,
                            Attributes = findData.Attributes,
                            Stamp = new LocalFileSystem.FileStamp(findData)
                        });
                    }
                }
                else if (flag)
                {
                    ISyncCustomization syncCustomization2 = _customization;
                    if ((syncCustomization2 == null || !syncCustomization2.IsFileIgnored(localPath, findData.FileName)) && IsIncluded(findData.FileName, _settings.FileFilters))
                    {
                        list.Add(new LocalEntry
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
                Entries = list.ToArray(),
                IsIncluded = flag
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
            for (int num = 0; num < result.Entries.Length; num++)
            {
                if (result.Entries[num].HasAttribute(FileAttributes.Directory) && !result.Entries[num].HasAttribute(FileAttributes.ReparsePoint))
                {
                    result.Entries[num].Children = ListRecursive(localPath.Combine(result.Entries[num].Name), relativeRemotePath.Combine(result.Entries[num].Name));
                }
            }
            return result;
        }

        private int AddFile(LocalPath localPath, long value2)
        {
            int count = Files.Count;
            Files.Add(localPath);
            base.Statistics.TotalBytes += value2;
            base.Statistics.TotalFiles++;
            return count;
        }

        private void PlanDirectory(LocalPath localPath, RemoteFileSet.RemoteDirectory remoteDirectory, RelativeRemotePath relativeRemotePath, LocalDirectoryListing localDirectoryListing)
        {
            Dictionary<string, RemoteEntryRef> dictionary = IndexRemoteDirectory(remoteDirectory);
            ICachedDirectory cachedDirectory = _cache?.GetDirectory(localPath);
            if (localDirectoryListing.Entries == null)
            {
                localDirectoryListing = ListDirectory(localPath, relativeRemotePath);
            }
            LocalEntry[] array = localDirectoryListing.Entries;
            for (int num = 0; num < array.Length; num++)
            {
                LocalEntry localEntry = array[num];
                LocalPath localPath2 = localPath.Combine(localEntry.Name);
                bool flag = dictionary.TryGetValue(localEntry.Name, out var value);
                if (flag)
                {
                    dictionary.Remove(localEntry.Name);
                }
                LocalFileSystem.FileStamp fileStamp = localEntry.Stamp;
                if (localEntry.HasAttribute(FileAttributes.ReparsePoint))
                {
                    try
                    {
                        localPath2 = LocalFileSystem.ResolveFinalSymlink(localPath2);
                    }
                    catch (Exception ex)
                    {
                        base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.UnreadableSourceSymlink, localPath2.FullPath, 0), ex);
                        continue;
                    }
                    string text = null;
                    VisitedDirectory value3;
                    if (localEntry.HasAttribute(FileAttributes.Directory))
                    {
                        if (_visited.TryGetValue(localPath2.FullPath, out var value2))
                        {
                            string text2 = "/" + relativeRemotePath.Path;
                            RelativeRemotePath relativeRemotePath2 = value2.RelativePath;
                            text = RemotePathUtils.GetRelativePath(text2, "/" + relativeRemotePath2.ToString(), flag: false);
                        }
                    }
                    else if (_visited.TryGetValue(localPath2.GetParent().FullPath, out value3) && value3.IsIncluded && IsIncluded(localPath2.FileName, _settings.FileFilters))
                    {
                        string text3 = "/" + relativeRemotePath.Path;
                        RelativeRemotePath relativeRemotePath3 = value3.RelativePath;
                        string text4 = RemotePathUtils.GetRelativePath(text3, "/" + relativeRemotePath3.ToString(), flag: false);
                        string text5 = localPath2.FileName;
                        text = ((!(text4 == "") && !(text4 == ".")) ? (text4 + "/" + text5) : text5);
                    }
                    else
                    {
                        try
                        {
                            using LocalFileSystem.NativeFile nativeFile = LocalFileSystem.OpenFile(localPath2, LocalFileSystem.FileOpenMode.Open);
                            fileStamp = new LocalFileSystem.FileStamp(nativeFile.GetSize(), nativeFile.GetLastWriteTime());
                        }
                        catch (Exception ex2)
                        {
                            base.ReportWarning(new QuickSyncWarning(QuickSyncWarningType.CannotStatLocalSymlink, localPath2.FullPath, 0), ex2);
                        }
                    }
                    if (text != null)
                    {
                        if (!flag || value.Kind != SyncPlan.UploadPlanner.RemoteEntryKind.Symlink || !(remoteDirectory.Symlinks[value.Index].Target == text))
                        {
                            if (flag)
                            {
                                PlanDeleteEntry(value, remoteDirectory, localEntry.Name);
                            }
                            bool flag2 = cachedDirectory?.IsSymlinkUpToDate(localEntry.Name, text) ?? true;
                            if (_settings.QuickSyncOverwriteTrigger != QuickSyncOverwriteTrigger.LocalCache || cachedDirectory == null || _remoteIsEmpty || flag2)
                            {
                                _plan.AddAction(ActionType.CreateRemoteSymlink, relativeRemotePath.Combine(localEntry.Name).ToString() + "\n" + text);
                            }
                        }
                        continue;
                    }
                }
                if (flag)
                {
                    if (localEntry.HasAttribute(FileAttributes.Directory))
                    {
                        if (value.Kind != SyncPlan.UploadPlanner.RemoteEntryKind.Directory)
                        {
                            PlanDeleteEntry(value, remoteDirectory, localEntry.Name);
                        }
                    }
                    else if (value.Kind != SyncPlan.UploadPlanner.RemoteEntryKind.File)
                    {
                        PlanDeleteEntry(value, remoteDirectory, localEntry.Name);
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
                                if (LocalFileSystem.FileStamp.IsSame(fileStamp, remoteDirectory.Files[value.Index].GetFileInfo()))
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
                    RelativeRemotePath relativeRemotePath4 = relativeRemotePath.Combine(localEntry.Name);
                    SyncPlan syncPlan = _plan;
                    string text6 = localEntry.Name;
                    bool num2 = flag && value.Kind == SyncPlan.UploadPlanner.RemoteEntryKind.Directory;
                    LocalFileSystem.FileStamp fileStamp2 = localEntry.Stamp;
                    using (syncPlan.EnterDirectory(text6, num2, fileStamp2.ModifiedSeconds))
                    {
                        RemoteFileSet.RemoteDirectory remoteDirectory2 = this._emptyDirectory;
                        if (flag && value.Kind == SyncPlan.UploadPlanner.RemoteEntryKind.Directory)
                        {
                            remoteDirectory2 = remoteDirectory.SubDirectories[value.Index];
                        }
                        PlanDirectory(localPath2, remoteDirectory2, relativeRemotePath4, localEntry.Children);
                    }
                }
                else
                {
                    bool flag3 = cachedDirectory?.IsFileUpToDate(localEntry.Name, fileStamp) ?? true;
                    if (_settings.QuickSyncOverwriteTrigger == QuickSyncOverwriteTrigger.LocalCache && cachedDirectory != null && !_remoteIsEmpty && !flag3)
                    {
                        base.Statistics.UpToDateFiles++;
                        base.Statistics.UpToDateBytes += fileStamp.Size;
                    }
                    else
                    {
                        int num3 = AddFile(localPath2, fileStamp.Size);
                        cachedDirectory?.Register(num3, localEntry.Name);
                        _plan.AddAction(ActionType.TransferFile, localEntry.Name, num3);
                    }
                }
            }
            if ((_settings.Options & SyncOptions.DeleteExtraneous) != SyncOptions.DeleteExtraneous || !localDirectoryListing.IsIncluded)
            {
                return;
            }
            foreach (KeyValuePair<string, RemoteEntryRef> item in dictionary)
            {
                PlanDeleteEntry(item.Value, remoteDirectory, item.Key);
            }
            if (_settings.QuickSyncOverwriteTrigger != QuickSyncOverwriteTrigger.LocalCache || cachedDirectory == null || _remoteIsEmpty)
            {
                return;
            }
            foreach (string item2 in cachedDirectory.GetEntryNames().EnsureNotNull())
            {
                if (IsIncluded(item2, _settings.FileFilters))
                {
                    string text7 = relativeRemotePath.PrependBase(_settings.RemotePath) + "/" + item2;
                    base.Statistics.FilesToDelete++;
                    _plan.AddAction(ActionType.DeleteTargetFile, text7);
                }
            }
        }

        private void DeleteDirectoryContents(RemoteFileSet.RemoteDirectory remoteDirectory, string text)
        {
            _plan.AddAction(ActionType.EnterDirectory, text);
            foreach (KeyValuePair<string, RemoteEntryRef> item in IndexRemoteDirectory(remoteDirectory))
            {
                PlanDeleteEntry(item.Value, remoteDirectory, item.Key);
            }
            _plan.AddAction(ActionType.GoToParentDirectory, null);
            _plan.AddAction(ActionType.DeleteEmptyTargetDirectory, text);
        }

        private void PlanDeleteEntry(RemoteEntryRef remoteEntryRef, RemoteFileSet.RemoteDirectory remoteDirectory, string text)
        {
            switch (remoteEntryRef.Kind)
            {
                case SyncPlan.UploadPlanner.RemoteEntryKind.File:
                case SyncPlan.UploadPlanner.RemoteEntryKind.Symlink:
                    _plan.AddAction(ActionType.DeleteTargetFile, text);
                    break;
                case SyncPlan.UploadPlanner.RemoteEntryKind.Directory:
                    if (_settings.FileFilters.IsNullOrEmpty() && _settings.DirectoryFilters.IsNullOrEmpty())
                    {
                        _plan.AddAction(ActionType.DeleteTargetDirectoryRecursively, text);
                    }
                    else
                    {
                        DeleteDirectoryContents(remoteDirectory.SubDirectories[remoteEntryRef.Index], text);
                    }
                    break;
            }
        }

        private static Dictionary<string, RemoteEntryRef> IndexRemoteDirectory(RemoteFileSet.RemoteDirectory remoteDirectory)
        {
            Dictionary<string, RemoteEntryRef> dictionary = new Dictionary<string, RemoteEntryRef>();
            for (int num = 0; num < remoteDirectory.Files.Count; num++)
            {
                dictionary[remoteDirectory.Files[num].Name] = new RemoteEntryRef(SyncPlan.UploadPlanner.RemoteEntryKind.File, num);
            }
            for (int num2 = 0; num2 < remoteDirectory.SubDirectories.Count; num2++)
            {
                dictionary[remoteDirectory.SubDirectories[num2].Name] = new RemoteEntryRef(SyncPlan.UploadPlanner.RemoteEntryKind.Directory, num2);
            }
            for (int num3 = 0; num3 < remoteDirectory.Symlinks.Count; num3++)
            {
                dictionary[remoteDirectory.Symlinks[num3].Name] = new RemoteEntryRef(SyncPlan.UploadPlanner.RemoteEntryKind.Symlink, num3);
            }
            return dictionary;
        }
    }

    private class DirectoryScope : IDisposable
    {
        private SyncPlan _plan;

        private int _depth;

        public DirectoryScope(SyncPlan syncPlan, int value2)
        {
            this._plan = syncPlan;
            this._depth = value2;
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

    private void AddAction(ActionType actionType, string text, int value2 = 0)
    {
        Actions.Add(new SyncAction(actionType, text, value2));
    }

    public IDisposable EnterDirectory(string text, bool flag, int value2)
    {
        AddAction(flag ? ActionType.EnterDirectory : ActionType.CreateAndEnterDirectory, text, value2);
        int count = Actions.Count;
        return new DirectoryScope(this, count);
    }
}
