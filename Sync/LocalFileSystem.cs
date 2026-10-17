using SmarTTY.Ssh.QuickSync;
using Microsoft.Win32.SafeHandles;
using SmarTTY.Native;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmarTTY.Sync;

public static class LocalFileSystem
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FindData
    {
        public FileAttributes Attributes;

        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;

        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;

        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;

        public uint FileSizeHigh;

        public uint FileSizeLow;

        public uint Reserved0;

        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;

        public long Size => MakeLong((int)FileSizeHigh, (int)FileSizeLow);

        public long LastWriteTimeTicks => MakeLong(LastWriteTime.dwHighDateTime, LastWriteTime.dwLowDateTime);

        public bool IsDirectory => HasAttribute(FileAttributes.Directory);

        public bool HasAttribute(FileAttributes fileAttributes)
        {
            return (Attributes & fileAttributes) != 0;
        }
    }

    [Flags]
    public enum MoveFileFlags
    {
        None = 0,
        ReplaceExisting = 1,
        CopyAllowed = 2,
        DelayUntilReboot = 4,
        WriteThrough = 8,
        CreateHardlink = 0x10,
        FailIfNotTrackable = 0x20
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct FileInformation
    {
        public uint Attributes;

        public ulong CreationTime;

        public ulong LastAccessTime;

        public ulong LastWriteTime;

        public uint VolumeSerialNumber;

        public ulong FileSize;

        public uint NumberOfLinks;

        public ulong FileIndex;
    }

    public struct FileStamp
    {
        public readonly long LastWriteFileTime;

        public readonly long Size;

        public int UnixSeconds => (int)((LastWriteFileTime - UnixEpochFileTime) / 10000000);

        public int ModifiedSeconds => UnixSeconds;

        public bool HasTimestamp => LastWriteFileTime != 0;

        public FileStamp(long size, int unixSeconds)
        {
            LastWriteFileTime = UnixEpoch.AddSeconds(unixSeconds).ToFileTimeUtc();
            Size = size;
        }

        public FileStamp(long size, long fileTime)
        {
            Size = size;
            LastWriteFileTime = fileTime;
        }

        public FileStamp(FindData findData)
        {
            LastWriteFileTime = MakeLongUnsigned((uint)findData.LastWriteTime.dwHighDateTime, (uint)findData.LastWriteTime.dwLowDateTime);
            Size = MakeLongUnsigned(findData.FileSizeHigh, findData.FileSizeLow);
        }

        [SpecialName]
        public static bool IsSame(FileStamp first, FileStamp second)
        {
            if (first.ModifiedSeconds == second.ModifiedSeconds)
            {
                return first.Size == second.Size;
            }
            return false;
        }

        [SpecialName]
        public static bool IsDifferent(FileStamp first, FileStamp second)
        {
            if (first.ModifiedSeconds == second.ModifiedSeconds)
            {
                return first.Size != second.Size;
            }
            return true;
        }
    }

    public enum FileOpenMode
    {
        Create,
        Open
    }

    public class NativeFile : IDisposable
    {
        private SafeFileHandle _handle;

        public NativeFile(SafeFileHandle safeFileHandle)
        {
            _handle = safeFileHandle;
        }

        public int Write(byte[] buffer, int count)
        {
            if (!WriteFile(this._handle, buffer, count, out var bytesWritten, IntPtr.Zero))
            {
                throw new Win32Exception();
            }
            return bytesWritten;
        }

        public int Read(byte[] buffer, int count)
        {
            if (!ReadFile(this._handle, buffer, count, out var bytesRead, IntPtr.Zero))
            {
                throw new Win32Exception();
            }
            return bytesRead;
        }

        public long GetSize()
        {
            if (!GetFileSizeEx(_handle, out var fileSize))
            {
                throw new Win32Exception();
            }
            return fileSize;
        }

        public void Dispose()
        {
            _handle.Dispose();
        }

        public void SetLastWriteTime(long fileTime)
        {
            if (!SetFileTime(_handle, IntPtr.Zero, IntPtr.Zero, ref fileTime))
            {
                throw new Win32Exception();
            }
        }

        public long GetLastWriteTime()
        {
            if (!GetFileTime(_handle, IntPtr.Zero, IntPtr.Zero, out var fileTime))
            {
                throw new Win32Exception();
            }
            return fileTime;
        }
    }

    [Flags]
    public enum LinkOptions
    {
        None = 0,
        SkipSymlinks = 1,
        SkipHardlinks = 2,
        DeleteExtraneous = 4
    }

    public const int MaxPath = 260;

    public const int AlternateNameLength = 14;

    public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

    public static readonly long UnixEpochFileTime = UnixEpoch.ToFileTimeUtc();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr FindFirstFile(string fileName, out FindData findData);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern bool FindNextFile(IntPtr handle, out FindData findData);

    [DllImport("kernel32.dll")]
    public static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetFileAttributes(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetFileAttributes(string fileName, [MarshalAs(UnmanagedType.U4)] int fileAttributes);

    [DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectory(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileFlags flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile([MarshalAs(UnmanagedType.LPTStr)] string fileName, [MarshalAs(UnmanagedType.U4)] uint desiredAccess, [MarshalAs(UnmanagedType.U4)] FileShare shareMode, IntPtr securityAttributes, [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition, [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeHandle safeHandle, [In] IntPtr overlapped, out int bytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeHandle safeHandle, IntPtr buffer, int count, out int bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeHandle safeHandle, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int count, out int bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeHandle safeHandle, [In][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int count, out int bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeHandle safeHandle, IntPtr creationTime, IntPtr lastAccessTime, ref long lastWriteTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileTime(SafeHandle safeHandle, IntPtr creationTime, IntPtr lastAccessTime, out long lastWriteTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle safeFileHandle, out FileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle safeFileHandle, out long fileSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteFile(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool CreateSymbolicLink(string symlinkFileName, string targetFileName, int flags);

    public static void CreateSymbolicLink(string symlinkPath, string targetPath)
    {
        if ((CreateSymbolicLink(symlinkPath, targetPath, 2) && IsAccessible(symlinkPath)) || (CreateSymbolicLink(symlinkPath, targetPath, 0) && IsAccessible(symlinkPath)))
        {
            return;
        }
        throw new Win32Exception("Failed to symlink " + symlinkPath + " to " + targetPath);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool CopyFile(string existingFileName, string newFileName, [MarshalAs(UnmanagedType.I1)] bool failIfExists);

    public static IEnumerable<FindData> EnumerateDirectory(LocalPath localPath, bool ignoreMissing)
    {
        IntPtr findHandle = FindFirstFile(localPath.FullPath + "\\*.*", out var findData);
        if (findHandle == (IntPtr)(-1))
        {
            int lastWin32Error = Marshal.GetLastWin32Error();
            if (!((lastWin32Error == 3) & ignoreMissing))
            {
                throw new Win32Exception(lastWin32Error);
            }
            yield break;
        }
        try
        {
            do
            {
                if (!(findData.FileName == ".") && !(findData.FileName == ".."))
                {
                    yield return findData;
                }
            }
            while (FindNextFile(findHandle, out findData));
        }
        finally
        {
            FindClose(findHandle);
        }
    }

    public static long MakeLong(int high, int low)
    {
        return (long)(((ulong)(uint)high << 32) | (uint)low);
    }

    public static long MakeLongUnsigned(uint high, uint low)
    {
        return (long)(((ulong)high << 32) | low);
    }

    public static void CreateDirectoryRecursive(LocalPath localPath)
    {
        if (CreateDirectory(localPath.FullPath, IntPtr.Zero))
        {
            return;
        }
        switch (Marshal.GetLastWin32Error())
        {
            case 183:
                return;
            case 3:
                CreateDirectoryRecursive(localPath.GetParent());
                if (CreateDirectory(localPath.FullPath, IntPtr.Zero))
                {
                    return;
                }
                break;
        }
        throw new Win32Exception();
    }

    public static bool IsAccessible(string path)
    {
        using SafeFileHandle safeFileHandle = CreateFile(path, 131072u, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
        return !safeFileHandle.IsInvalid;
    }

    public static NativeFile OpenFile(LocalPath localPath, FileOpenMode fileOpenMode)
    {
        SafeFileHandle safeFileHandle = fileOpenMode switch
        {
            FileOpenMode.Create => CreateFile(localPath.FullPath, 1073741824u, FileShare.Read, IntPtr.Zero, FileMode.Create, FileAttributes.Normal, IntPtr.Zero),
            FileOpenMode.Open => CreateFile(localPath.FullPath, 2147483648u, FileShare.Read, IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero),
            _ => throw new ArgumentException("mode"),
        };
        if (safeFileHandle.IsInvalid)
        {
            throw new Win32Exception();
        }
        return new NativeFile(safeFileHandle);
    }

    public static FileInformation GetFileInformation(LocalPath localPath, out long fileSize, bool throwOnError)
    {
        fileSize = 0L;
        using SafeFileHandle safeFileHandle = CreateFile(localPath.FullPath, 2147483648u, FileShare.Read, IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
        if (safeFileHandle.IsInvalid || !GetFileInformationByHandle(safeFileHandle, out var fileInfo) || !GetFileSizeEx(safeFileHandle, out fileSize))
        {
            if (throwOnError)
            {
                throw new Win32Exception();
            }
            return default;
        }
        return fileInfo;
    }

    public static void CreateFileLink(LocalPath localPath, string relativeTarget, LinkOptions linkOptions, Action<RemoteWarningRecord> warningHandler)
    {
        LocalPath targetLocalPath = localPath.GetParent().Combine(relativeTarget);
        int fileAttributes = GetFileAttributes(localPath.FullPath);
        if (fileAttributes != -1 && (fileAttributes & 0x400) != 0 && (fileAttributes & 0x10) == 0)
        {
            KeyValuePair<string, bool> linkInfo = ReparsePoints.ReadReparsePoint(localPath.FullPath);
            if (StringComparer.InvariantCulture.Compare(linkInfo.Key, relativeTarget) == 0)
            {
                return;
            }
        }
        if (fileAttributes != -1 && (fileAttributes & 0x400) == 0)
        {
            FileInformation fileInfo1 = LocalFileSystem.GetFileInformation(localPath, out var size1, throwOnError: false);
            FileInformation fileInfo2 = LocalFileSystem.GetFileInformation(targetLocalPath, out var size2, throwOnError: false);
            if (fileInfo1.Attributes != 0 && fileInfo2.Attributes != 0 && ((fileInfo1.NumberOfLinks > 1 && fileInfo1.VolumeSerialNumber == fileInfo2.VolumeSerialNumber && fileInfo1.FileIndex == fileInfo2.FileIndex) || (fileInfo1.LastWriteTime == fileInfo2.LastWriteTime && size1 == size2)))
            {
                return;
            }
        }
        if (fileAttributes != -1)
        {
            if ((fileAttributes & 0x10) == 0)
            {
                if (!DeleteFile(localPath.FullPath))
                {
                    warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteFile, localPath.FullPath, Marshal.GetLastWin32Error()));
                }
            }
            else if ((fileAttributes & 0x400) == 0)
            {
                DeleteDirectoryRecursive(localPath, warningHandler);
            }
            else if (!RemoveDirectory(localPath.FullPath))
            {
                warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteDirectory, localPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        try
        {
            CreateDirectoryRecursive(localPath.GetParent());
        }
        catch
        {
            warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateDirectory, localPath.GetParent().FullPath, Marshal.GetLastWin32Error()));
            return;
        }
        if ((linkOptions & LinkOptions.SkipSymlinks) == 0)
        {
            try
            {
                CreateSymbolicLink(localPath.FullPath, relativeTarget.Replace('/', '\\'));
                return;
            }
            catch
            {
                warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateSymlink, localPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        if ((linkOptions & LinkOptions.SkipHardlinks) == 0)
        {
            try
            {
                if (!CreateHardLink(localPath.FullPath, targetLocalPath.FullPath, IntPtr.Zero))
                {
                    throw new Win32Exception($"Failed to hardlink {localPath} to {targetLocalPath}");
                }
                return;
            }
            catch
            {
                warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateHardlink, localPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        try
        {
            if (!CopyFile(localPath.FullPath, targetLocalPath.FullPath, failIfExists: false))
            {
                throw new Win32Exception($"Failed to copy {targetLocalPath} to {localPath}");
            }
        }
        catch
        {
            warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.CopyFile, localPath.FullPath, Marshal.GetLastWin32Error()));
        }
    }

    private static LocalPath ResolveSymlinkTarget(LocalPath localPath)
    {
        KeyValuePair<string, bool> linkInfo = ReparsePoints.ReadReparsePoint(localPath.FullPath);
        string target = linkInfo.Key ?? "";
        if (target.StartsWith("\\\\?\\"))
        {
            target = target.Substring(4);
        }
        if (!linkInfo.Value || target.StartsWith("\\\\") || (target.Length >= 2 && target[1] == ':'))
        {
            return LocalPath.FromUserPath(linkInfo.Key);
        }
        return localPath.GetParent().Combine(target);
    }

    public static LocalPath ResolveFinalSymlink(LocalPath localPath)
    {
        HashSet<string> visitedPaths = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        int attributes;
        do
        {
            visitedPaths.Add(localPath.FullPath);
            localPath = ResolveSymlinkTarget(localPath);
            if (visitedPaths.Contains(localPath.FullPath))
            {
                throw new Exception("Recursive symlink: " + localPath.FullPath);
            }
            attributes = GetFileAttributes(localPath.FullPath);
        }
        while (attributes != -1 && (attributes & 0x400) != 0);
        return localPath;
    }

    public static void MoveFile(LocalPath sourcePath, LocalPath destPath, MoveFileFlags moveFileFlags = MoveFileFlags.None)
    {
        if (!MoveFileEx(sourcePath.FullPath, destPath.FullPath, moveFileFlags))
        {
            throw new Win32Exception();
        }
    }

    public static void DeleteDirectoryRecursive(LocalPath localPath, Action<RemoteWarningRecord> warningHandler)
    {
        foreach (FindData childData in EnumerateDirectory(localPath, ignoreMissing: false))
        {
            LocalPath childPath = localPath.Combine(childData.FileName);
            if (childData.HasAttribute(FileAttributes.Directory))
            {
                if (!childData.HasAttribute(FileAttributes.ReparsePoint))
                {
                    DeleteDirectoryRecursive(childPath, warningHandler);
                }
                else if (!RemoveDirectory(childPath.FullPath))
                {
                    warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteDirectory, childPath.FullPath, Marshal.GetLastWin32Error()));
                }
            }
            else if (!TryDeleteFile(childPath, resetReadOnly: true, ignoreMissing: false))
            {
                warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteFile, childPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        if (!RemoveDirectory(localPath.FullPath))
        {
            warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteDirectory, localPath.FullPath, Marshal.GetLastWin32Error()));
        }
    }

    private static bool TryDeleteFile(LocalPath localPath, bool resetReadOnly, bool ignoreMissing)
    {
        if (DeleteFile(localPath.FullPath))
        {
            return true;
        }
        int lastWin32Error = Marshal.GetLastWin32Error();
        if ((lastWin32Error == 2) & ignoreMissing)
        {
            return true;
        }
        if ((lastWin32Error == 5) & resetReadOnly)
        {
            int attributes = GetFileAttributes(localPath.FullPath);
            if (attributes != -1 && (attributes & 1) != 0)
            {
                SetFileAttributes(localPath.FullPath, attributes & -2);
                if (DeleteFile(localPath.FullPath))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static void DeleteFile(LocalPath localPath, bool resetReadOnly, bool ignoreMissing = false)
    {
        if (TryDeleteFile(localPath, resetReadOnly, ignoreMissing))
        {
            return;
        }
        throw new Win32Exception("Failed to delete " + localPath.ToString());
    }

    public static void DeleteDirectory(LocalPath localPath)
    {
        if (RemoveDirectory(localPath.FullPath))
        {
            return;
        }
        if (Marshal.GetLastWin32Error() == 5)
        {
            int attributes = GetFileAttributes(localPath.FullPath);
            if (attributes != -1 && (attributes & 1) != 0)
            {
                SetFileAttributes(localPath.FullPath, attributes & -2);
                if (RemoveDirectory(localPath.FullPath))
                {
                    return;
                }
            }
        }
        throw new Win32Exception("Failed to delete " + localPath.ToString());
    }

    public static void CreateDirectoryLink(LocalPath localPath, string targetPath, LinkOptions linkOptions, Action<RemoteWarningRecord> warningHandler)
    {
        int fileAttributes = GetFileAttributes(localPath.FullPath);
        if (fileAttributes != -1 && (fileAttributes & 0x400) != 0 && (fileAttributes & 0x10) != 0)
        {
            KeyValuePair<string, bool> linkInfo = ReparsePoints.ReadReparsePoint(localPath.FullPath);
            if (StringComparer.InvariantCulture.Compare(linkInfo.Key, targetPath) == 0)
            {
                return;
            }
        }
        if (fileAttributes != -1 && (fileAttributes & 0x10) == 0 && !DeleteFile(localPath.FullPath))
        {
            warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteFile, localPath.FullPath, Marshal.GetLastWin32Error()));
        }
        try
        {
            CreateDirectoryRecursive(localPath);
        }
        catch
        {
            warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateDirectory, localPath.GetParent().FullPath, Marshal.GetLastWin32Error()));
            return;
        }
        if ((linkOptions & LinkOptions.SkipSymlinks) == 0)
        {
            try
            {
                ReparsePoints.CreateSymbolicLink(localPath.FullPath, targetPath.Replace('/', '\\'), isDirectory: true);
                return;
            }
            catch
            {
                warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateSymlink, localPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        if (targetPath.Contains("."))
        {
            LocalPath relativeParentPath = localPath.Combine("..\\" + targetPath.Replace('/', '\\'));
            if (localPath.FullPath.ToLower() == relativeParentPath.FullPath.ToLower() || localPath.FullPath.StartsWith(relativeParentPath.FullPath.TrimEnd('\\') + "\\", StringComparison.InvariantCultureIgnoreCase))
            {
                warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.RecursiveLink, localPath.FullPath, Marshal.GetLastWin32Error()));
                return;
            }
        }
        Dictionary<string, FindData> childDict = EnumerateDirectory(localPath, ignoreMissing: true).ToOverridingDictionary((FindData findData) => findData.FileName, StringComparer.InvariantCultureIgnoreCase);
        foreach (FindData childData in EnumerateDirectory(localPath.GetParent().Combine(targetPath), ignoreMissing: true))
        {
            childDict.Remove(childData.FileName);
            string subTargetPath = "..\\" + targetPath + "\\" + childData.FileName;
            if (childData.HasAttribute(FileAttributes.Directory))
            {
                CreateDirectoryLink(localPath.Combine(childData.FileName), subTargetPath, linkOptions | LinkOptions.SkipSymlinks, warningHandler);
            }
            else
            {
                CreateFileLink(localPath.Combine(childData.FileName), subTargetPath, linkOptions | LinkOptions.SkipSymlinks, warningHandler);
            }
        }
        if ((linkOptions & LinkOptions.DeleteExtraneous) != LinkOptions.DeleteExtraneous)
        {
            return;
        }
        foreach (FindData extraneousData in childDict.Values)
        {
            LocalPath extraneousPath = localPath.Combine(extraneousData.FileName);
            if (!extraneousData.HasAttribute(FileAttributes.Directory))
            {
                if (!DeleteFile(extraneousPath.FullPath))
                {
                    warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteFile, extraneousPath.FullPath, Marshal.GetLastWin32Error()));
                }
            }
            else if (!extraneousData.HasAttribute(FileAttributes.ReparsePoint))
            {
                DeleteDirectoryRecursive(extraneousPath, warningHandler);
            }
            else if (!RemoveDirectory(extraneousPath.FullPath))
            {
                warningHandler?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteDirectory, extraneousPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
    }
}
