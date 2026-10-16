using SmarTTY.IO;
using SmarTTY.Sync;
using System;
using System.Collections.Generic;
using System.IO;

namespace ManagedLibSSH2.QuickSync;

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

        public string[] AllFilePaths
        {
            get
            {
                List<string> list = new List<string>();
                CollectFilePaths(list, "");
                return list.ToArray();
            }
        }

        public IEnumerable<RemoteDirectory> Descendants
        {
            get
            {
                yield return this;
                foreach (RemoteDirectory item in SubDirectories)
                {
                    foreach (RemoteDirectory item2 in item.Descendants)
                    {
                        yield return item2;
                    }
                }
            }
        }

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

        public RemoteDirectory(RemoteDirectory remoteDirectory, string text)
        {
            Parent = remoteDirectory;
            Name = text;
        }

        private void CollectFilePaths(List<string> list, string text)
        {
            foreach (RemoteDirectory item in SubDirectories)
            {
                item.CollectFilePaths(list, item.Name + "/");
            }
            foreach (RemoteFileEntry item2 in Files)
            {
                list.Add(text + item2.Name);
            }
        }

        public override string ToString()
        {
            return FullPath ?? "(root)";
        }

        public string Combine(string text)
        {
            if (string.IsNullOrEmpty(FullPath))
            {
                return text;
            }
            return FullPath + "/" + text;
        }

        public void Dump(StreamWriter streamWriter, string text2 = "", int value2 = 1)
        {
            string text = "/";
            streamWriter.WriteLine($"<{value2}>{text2}{Name} ({FullPath ?? text}):");
            text2 += "  ";
            foreach (RemoteFileEntry item in Files)
            {
                streamWriter.WriteLine($"{text2}{item.Name} (size={item.Size}, time={item.ModifiedSeconds}, id={item.Id})");
            }
            foreach (RemoteSymlink item2 in this.Symlinks)
            {
                streamWriter.WriteLine(text2 + item2.Name + " => " + item2.Target + (item2.IsDirectory ? "(dir)" : "(file)"));
            }
            foreach (RemoteDirectory item3 in SubDirectories)
            {
                item3.Dump(streamWriter, text2, value2 + 1);
            }
        }
    }

    public struct QueuedSymlink(RemoteDirectory remoteDirectory, string text)
    {
        public RemoteDirectory Directory = remoteDirectory;

        public string Name = text;
    }

    internal class RemoteFileSetReader
    {
        private struct FileLocation
        {
            public readonly RemoteDirectory Directory;

            public readonly string Name;

            public string FullPath => Directory.Combine(Name);

            public FileLocation(RemoteDirectory remoteDirectory, string text)
            {
                Directory = remoteDirectory;
                this.Name = text;
            }
        }

        public class RemoteFileSetFormatException : Exception
        {
            public RemoteFileSetFormatException(string text)
                : base(text)
            {
            }
        }

        private SyncStreamReader _reader;

        private readonly int _timeOffsetSeconds;

        private List<RemoteDirectory> _directories = new List<RemoteDirectory>();

        private List<QueuedSymlink> _queuedSymlinks = new List<QueuedSymlink>();

        private List<FileLocation> _files = new List<FileLocation>();

        public event Action<QuickSyncWarning> Warning;

        public RemoteFileSetReader(SyncStreamReader syncStreamReader, int value2)
        {
            this._reader = syncStreamReader;
            _timeOffsetSeconds = value2;
        }

        private void RegisterFile(int value2, RemoteDirectory remoteDirectory, string text)
        {
            if (value2 >= 0)
            {
                while (value2 >= _files.Count)
                {
                    _files.Add(default);
                }
                _files[value2] = new FileLocation(remoteDirectory, text);
            }
        }

        private RemoteDirectory GetOrCreateDirectory(int value2, RemoteDirectory remoteDirectory, string text, bool flag = false)
        {
            while (value2 >= _directories.Count)
            {
                _directories.Add(null);
            }
            if (_directories[value2] != null)
            {
                if (flag)
                {
                    throw new RemoteFileSetFormatException($"Directory #{value2} already has a body");
                }
                return _directories[value2];
            }
            return _directories[value2] = new RemoteDirectory(remoteDirectory, text);
        }

        private RemoteDirectory GetDirectory(int value2)
        {
            if (value2 < 0 || value2 >= _directories.Count || _directories[value2] == null)
            {
                throw new RemoteFileSetFormatException("Unexpected directory ID: " + value2);
            }
            return _directories[value2];
        }

        public RemoteFileSet Read()
        {
            SyncRecordType syncRecordType = _reader.ReadRecordType();
            if (syncRecordType == SyncRecordType.FileSetStart)
            {
                RemoteDirectory remoteDirectory = GetOrCreateDirectory(_reader.ReadInt32(), null, null);
                while (true)
                {
                    syncRecordType = _reader.ReadRecordType();
                    switch (syncRecordType)
                    {
                        case SyncRecordType.DirectoryBody:
                            {
                                int num3 = _reader.ReadInt32();
                                ReadDirectoryBody(GetDirectory(num3));
                                break;
                            }
                        case SyncRecordType.DirectoryError:
                            {
                                int num4 = _reader.ReadInt32();
                                int num5 = _reader.ReadInt32();
                                RemoteDirectory remoteDirectory2 = GetDirectory(num4);
                                if (remoteDirectory2 != remoteDirectory || num5 != 2)
                                {
                                    Warning?.Invoke(new QuickSyncWarning(QuickSyncWarningType.UnreadableRemoteDirectory, remoteDirectory2.FullPath, num5));
                                }
                                break;
                            }
                        case SyncRecordType.ResolvedSymlinkToFile:
                        case SyncRecordType.ResolvedSymlinkInTree:
                        case SyncRecordType.ResolvedSymlinkToDirectory:
                        case SyncRecordType.ResolvedSymlinkDirectoryBody:
                            {
                                int num = _reader.ReadInt32();
                                int num2 = _reader.ReadInt32();
                                if (num < 0 || num >= _queuedSymlinks.Count || _queuedSymlinks[num].Directory == null)
                                {
                                    throw new RemoteFileSetFormatException("Unexpected queued symlink ID: " + num);
                                }
                                QueuedSymlink queuedSymlink = _queuedSymlinks[num];
                                switch (syncRecordType)
                                {
                                    case SyncRecordType.ResolvedSymlinkDirectoryBody:
                                        {
                                            RemoteDirectory item = GetOrCreateDirectory(num2, queuedSymlink.Directory, queuedSymlink.Name, flag: true);
                                            queuedSymlink.Directory.SubDirectories.Add(item);
                                            break;
                                        }
                                    case SyncRecordType.ResolvedSymlinkToDirectory:
                                        {
                                            RemoteDirectory remoteDirectory3 = GetDirectory(num2);
                                            queuedSymlink.Directory.Symlinks.Add(new RemoteDirectory.RemoteSymlink
                                            {
                                                Name = queuedSymlink.Name,
                                                IsDirectory = true,
                                                Target = RemotePathUtils.GetRelativePath("/" + queuedSymlink.Directory.FullPath, "/" + remoteDirectory3.FullPath, flag: false)
                                            });
                                            break;
                                        }
                                    case SyncRecordType.ResolvedSymlinkToFile:
                                        if (num2 < 0 || num2 >= _files.Count || _files[num2].Directory == null)
                                        {
                                            throw new RemoteFileSetFormatException("Invalid target file ID: " + num2);
                                        }
                                        queuedSymlink.Directory.Symlinks.Add(new RemoteDirectory.RemoteSymlink
                                        {
                                            Name = queuedSymlink.Name,
                                            IsDirectory = false,
                                            Target = RemotePathUtils.GetRelativePath("/" + queuedSymlink.Directory.FullPath, "/" + _files[num2].FullPath, flag: false)
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
                                Root = remoteDirectory
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
                            int num4 = this._reader.ReadInt32();
                            string text6 = this._reader.ReadString();
                            while (num4 >= _queuedSymlinks.Count)
                            {
                                _queuedSymlinks.Add(default);
                            }
                            _queuedSymlinks[num4] = new QueuedSymlink(remoteDirectory, text6);
                            break;
                        }
                    case SyncRecordType.DirectorySymlink:
                        {
                            int num3 = this._reader.ReadInt32();
                            string text5 = this._reader.ReadString();
                            RemoteDirectory remoteDirectory2 = GetDirectory(num3);
                            remoteDirectory.Symlinks.Add(new RemoteDirectory.RemoteSymlink
                            {
                                IsDirectory = true,
                                Name = text5,
                                Target = RemotePathUtils.GetRelativePath("/" + remoteDirectory.FullPath, "/" + remoteDirectory2.FullPath, flag: false)
                            });
                            break;
                        }
                    case SyncRecordType.Directory:
                        {
                            int num2 = this._reader.ReadInt32();
                            string text4 = this._reader.ReadString();
                            RemoteDirectory item = GetOrCreateDirectory(num2, remoteDirectory, text4);
                            remoteDirectory.SubDirectories.Add(item);
                            break;
                        }
                    case SyncRecordType.UnreadableSymlink:
                        {
                            int num = this._reader.ReadInt32();
                            string text3 = this._reader.ReadString();
                            Warning?.Invoke(new QuickSyncWarning(QuickSyncWarningType.UnreadableSourceSymlink, remoteDirectory.Combine(text3), num));
                            break;
                        }
                    case SyncRecordType.FileSymlink:
                    case SyncRecordType.DirectorySymlinkText:
                        {
                            string text = this._reader.ReadString();
                            string text2 = this._reader.ReadString();
                            remoteDirectory.Symlinks.Add(new RemoteDirectory.RemoteSymlink
                            {
                                Name = text,
                                Target = text2,
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
            RemoteFileEntry result = new RemoteFileEntry
            {
                Size = this._reader.ReadInt64(),
                ModifiedSeconds = this._reader.ReadInt32() - _timeOffsetSeconds,
                Id = this._reader.ReadInt32(),
                Name = this._reader.ReadString()
            };
            RegisterFile(result.Id, remoteDirectory, result.Name);
            return result;
        }
    }

    public RemoteDirectory Root;
}
