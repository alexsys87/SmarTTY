using SmarTTY.IO;
using SmarTTY.Sync;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.Ssh.QuickSync;

public class RemoteFileSet
{
    public struct RemoteFileEntry
    {
        public int Id;

        public string Name;

        public int ModifiedSeconds;

        public long Size;

        public override string ToString()
        {
            return Name;
        }

        public LocalFileSystem.FileStamp GetFileInfo()
        {
            return new LocalFileSystem.FileStamp(Size, LocalFileSystem.UnixEpoch.AddSeconds(ModifiedSeconds).ToFileTimeUtc());
        }
    }

    public class RemoteDirectory
    {
        public struct RemoteSymlink
        {
            public string Name;

            public bool IsDirectory;

            public string Target;
        }

        public readonly string Name;

        public readonly RemoteDirectory Parent;

        public List<RemoteFileEntry> Files = new List<RemoteFileEntry>();

        public List<RemoteDirectory> SubDirectories = new List<RemoteDirectory>();

        public List<RemoteSymlink> Symlinks = new List<RemoteSymlink>();

        public string FullPath
        {
            get
            {
                if (Parent?.Name != null)
                {
                    return Parent.FullPath + "/" + Name;
                }
                return Name;
            }
        }

        public RemoteDirectory(RemoteDirectory parent, string dirName)
        {
            Parent = parent;
            Name = dirName;
        }

        public override string ToString()
        {
            return FullPath ?? "(root)";
        }

        public string Combine(string childName)
        {
            if (string.IsNullOrEmpty(FullPath))
            {
                return childName;
            }
            return FullPath + "/" + childName;
        }

        public void Dump(StreamWriter writer, string indent = "", int depth = 1)
        {
            string rootSymbol = "/";
            writer.WriteLine($"<{depth}>{indent}{Name} ({FullPath ?? rootSymbol}):");
            indent += "  ";
            foreach (RemoteFileEntry file in Files)
            {
                writer.WriteLine($"{indent}{file.Name} (size={file.Size}, time={file.ModifiedSeconds}, id={file.Id})");
            }
            foreach (RemoteSymlink symlink in this.Symlinks)
            {
                writer.WriteLine(indent + symlink.Name + " => " + symlink.Target + (symlink.IsDirectory ? "(dir)" : "(file)"));
            }
            foreach (RemoteDirectory subDir in SubDirectories)
            {
                subDir.Dump(writer, indent, depth + 1);
            }
        }
    }

    public struct QueuedSymlink(RemoteDirectory remoteDirectory, string linkName)
    {
        public RemoteDirectory Directory = remoteDirectory;

        public string Name = linkName;
    }

    internal class RemoteFileSetReader
    {
        private struct FileLocation
        {
            public readonly RemoteDirectory Directory;

            public readonly string Name;

            public string FullPath => Directory.Combine(Name);

            public FileLocation(RemoteDirectory remoteDirectory, string fileName)
            {
                Directory = remoteDirectory;
                this.Name = fileName;
            }
        }

        public class RemoteFileSetFormatException : Exception
        {
            public RemoteFileSetFormatException(string message)
                : base(message)
            {
            }
        }

        private SyncStreamReader _reader;

        private readonly int _timeOffsetSeconds;

        private List<RemoteDirectory> _directories = new List<RemoteDirectory>();

        private List<QueuedSymlink> _queuedSymlinks = new List<QueuedSymlink>();

        private List<FileLocation> _files = new List<FileLocation>();

        public event Action<QuickSyncWarning> Warning;

        public RemoteFileSetReader(SyncStreamReader syncStreamReader, int timeOffsetSeconds)
        {
            this._reader = syncStreamReader;
            _timeOffsetSeconds = timeOffsetSeconds;
        }

        private void RegisterFile(int fileId, RemoteDirectory remoteDirectory, string fileName)
        {
            if (fileId >= 0)
            {
                while (fileId >= _files.Count)
                {
                    _files.Add(default);
                }
                _files[fileId] = new FileLocation(remoteDirectory, fileName);
            }
        }

        private RemoteDirectory GetOrCreateDirectory(int dirId, RemoteDirectory parent, string dirName, bool isBody = false)
        {
            while (dirId >= _directories.Count)
            {
                _directories.Add(null);
            }
            if (_directories[dirId] != null)
            {
                if (isBody)
                {
                    throw new RemoteFileSetFormatException($"Directory #{dirId} already has a body");
                }
                return _directories[dirId];
            }
            return _directories[dirId] = new RemoteDirectory(parent, dirName);
        }

        private RemoteDirectory GetDirectory(int dirId)
        {
            if (dirId < 0 || dirId >= _directories.Count || _directories[dirId] == null)
            {
                throw new RemoteFileSetFormatException("Unexpected directory ID: " + dirId);
            }
            return _directories[dirId];
        }

        public RemoteFileSet Read()
        {
            SyncRecordType syncRecordType = _reader.ReadRecordType();
            if (syncRecordType == SyncRecordType.FileSetStart)
            {
                RemoteDirectory rootDirectory = GetOrCreateDirectory(_reader.ReadInt32(), null, null);
                while (true)
                {
                    syncRecordType = _reader.ReadRecordType();
                    switch (syncRecordType)
                    {
                        case SyncRecordType.DirectoryBody:
                            {
                                int dirId = _reader.ReadInt32();
                                ReadDirectoryBody(GetDirectory(dirId));
                                break;
                            }
                        case SyncRecordType.DirectoryError:
                            {
                                int dirId = _reader.ReadInt32();
                                int errCode = _reader.ReadInt32();
                                RemoteDirectory errorDir = GetDirectory(dirId);
                                if (errorDir != rootDirectory || errCode != 2)
                                {
                                    Warning?.Invoke(new QuickSyncWarning(QuickSyncWarningType.UnreadableRemoteDirectory, errorDir.FullPath, errCode));
                                }
                                break;
                            }
                        case SyncRecordType.ResolvedSymlinkToFile:
                        case SyncRecordType.ResolvedSymlinkInTree:
                        case SyncRecordType.ResolvedSymlinkToDirectory:
                        case SyncRecordType.ResolvedSymlinkDirectoryBody:
                            {
                                int symlinkId = _reader.ReadInt32();
                                int targetId = _reader.ReadInt32();
                                if (symlinkId < 0 || symlinkId >= _queuedSymlinks.Count || _queuedSymlinks[symlinkId].Directory == null)
                                {
                                    throw new RemoteFileSetFormatException("Unexpected queued symlink ID: " + symlinkId);
                                }
                                QueuedSymlink queuedSymlink = _queuedSymlinks[symlinkId];
                                switch (syncRecordType)
                                {
                                    case SyncRecordType.ResolvedSymlinkDirectoryBody:
                                        {
                                            RemoteDirectory subDir = GetOrCreateDirectory(targetId, queuedSymlink.Directory, queuedSymlink.Name, isBody: true);
                                            queuedSymlink.Directory.SubDirectories.Add(subDir);
                                            break;
                                        }
                                    case SyncRecordType.ResolvedSymlinkToDirectory:
                                        {
                                            RemoteDirectory targetDir = GetDirectory(targetId);
                                            queuedSymlink.Directory.Symlinks.Add(new RemoteDirectory.RemoteSymlink
                                            {
                                                Name = queuedSymlink.Name,
                                                IsDirectory = true,
                                                Target = RemotePathUtils.GetRelativePath("/" + queuedSymlink.Directory.FullPath, "/" + targetDir.FullPath, allowAbsoluteFallback: false)
                                            });
                                            break;
                                        }
                                    case SyncRecordType.ResolvedSymlinkToFile:
                                        if (targetId < 0 || targetId >= _files.Count || _files[targetId].Directory == null)
                                        {
                                            throw new RemoteFileSetFormatException("Invalid target file ID: " + targetId);
                                        }
                                        queuedSymlink.Directory.Symlinks.Add(new RemoteDirectory.RemoteSymlink
                                        {
                                            Name = queuedSymlink.Name,
                                            IsDirectory = false,
                                            Target = RemotePathUtils.GetRelativePath("/" + queuedSymlink.Directory.FullPath, "/" + _files[targetId].FullPath, allowAbsoluteFallback: false)
                                        });
                                        break;
                                    case SyncRecordType.ResolvedSymlinkInTree:
                                        if (_reader.ReadRecordType() != SyncRecordType.File)
                                        {
                                            throw new RemoteFileSetFormatException("In-tree symlink not followed by file record");
                                        }
                                        queuedSymlink.Directory.Files.Add(ReadFileEntry(queuedSymlink.Directory));
                                        break;
                                }
                                break;
                            }
                        case SyncRecordType.Padding:
                            _reader.ReadInt32();
                            break;
                        default:
                            throw new RemoteFileSetFormatException("Unexpected root-level record in remote file set: " + syncRecordType);
                        case SyncRecordType.EndOfFileSet:
                            return new RemoteFileSet
                            {
                                Root = rootDirectory
                            };
                    }
                }
            }
            throw new RemoteFileSetFormatException("Unexpected remote file set start: " + syncRecordType);
        }

        private void ReadDirectoryBody(RemoteDirectory remoteDirectory)
        {
            while (true)
            {
                SyncRecordType syncRecordType = this._reader.ReadRecordType();
                switch (syncRecordType)
                {
                    case SyncRecordType.File:
                        remoteDirectory.Files.Add(ReadFileEntry(remoteDirectory));
                        break;
                    case SyncRecordType.QueuedSymlink:
                        {
                            int symlinkId = this._reader.ReadInt32();
                            string linkName = this._reader.ReadString();
                            while (symlinkId >= _queuedSymlinks.Count)
                            {
                                _queuedSymlinks.Add(default);
                            }
                            _queuedSymlinks[symlinkId] = new QueuedSymlink(remoteDirectory, linkName);
                            break;
                        }
                    case SyncRecordType.DirectorySymlink:
                        {
                            int targetDirId = this._reader.ReadInt32();
                            string symlinkName = this._reader.ReadString();
                            RemoteDirectory targetDir = GetDirectory(targetDirId);
                            remoteDirectory.Symlinks.Add(new RemoteDirectory.RemoteSymlink
                            {
                                IsDirectory = true,
                                Name = symlinkName,
                                Target = RemotePathUtils.GetRelativePath("/" + remoteDirectory.FullPath, "/" + targetDir.FullPath, allowAbsoluteFallback: false)
                            });
                            break;
                        }
                    case SyncRecordType.Directory:
                        {
                            int subDirId = this._reader.ReadInt32();
                            string subDirName = this._reader.ReadString();
                            RemoteDirectory subDir = GetOrCreateDirectory(subDirId, remoteDirectory, subDirName);
                            remoteDirectory.SubDirectories.Add(subDir);
                            break;
                        }
                    case SyncRecordType.UnreadableSymlink:
                        {
                            int errCode = this._reader.ReadInt32();
                            string symlinkName = this._reader.ReadString();
                            Warning?.Invoke(new QuickSyncWarning(QuickSyncWarningType.UnreadableSourceSymlink, remoteDirectory.Combine(symlinkName), errCode));
                            break;
                        }
                    case SyncRecordType.FileSymlink:
                    case SyncRecordType.DirectorySymlinkText:
                        {
                            string symlinkName = this._reader.ReadString();
                            string targetPath = this._reader.ReadString();
                            remoteDirectory.Symlinks.Add(new RemoteDirectory.RemoteSymlink
                            {
                                Name = symlinkName,
                                Target = targetPath,
                                IsDirectory = (syncRecordType == SyncRecordType.DirectorySymlinkText)
                            });
                            break;
                        }
                    default:
                        throw new RemoteFileSetFormatException("Unexpected directory entry in remote file set: " + syncRecordType);
                    case SyncRecordType.EndOfDirectory:
                        return;
                }
            }
        }

        private RemoteFileEntry ReadFileEntry(RemoteDirectory remoteDirectory)
        {
            RemoteFileEntry entry = new RemoteFileEntry
            {
                Size = this._reader.ReadInt64(),
                ModifiedSeconds = this._reader.ReadInt32() - _timeOffsetSeconds,
                Id = this._reader.ReadInt32(),
                Name = this._reader.ReadString()
            };
            RegisterFile(entry.Id, remoteDirectory, entry.Name);
            return entry;
        }
    }

    public RemoteDirectory Root;
}
