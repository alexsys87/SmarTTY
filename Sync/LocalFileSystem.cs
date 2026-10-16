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

        public FileStamp(long value2, int value3)
        {
            LastWriteFileTime = UnixEpoch.AddSeconds(value3).ToFileTimeUtc();
            Size = value2;
        }

        public FileStamp(long value2, long value3)
        {
            Size = value2;
            LastWriteFileTime = value3;
        }

        public FileStamp(FindData findData)
        {
            LastWriteFileTime = MakeLongUnsigned((uint)findData.LastWriteTime.dwHighDateTime, (uint)findData.LastWriteTime.dwLowDateTime);
            Size = MakeLongUnsigned(findData.FileSizeHigh, findData.FileSizeLow);
        }

        [SpecialName]
        public static bool IsSame(FileStamp fileStamp, FileStamp fileStamp2)
        {
            if (fileStamp.ModifiedSeconds == fileStamp2.ModifiedSeconds)
            {
                return fileStamp.Size == fileStamp2.Size;
            }
            return false;
        }

        [SpecialName]
        public static bool IsDifferent(FileStamp fileStamp, FileStamp fileStamp2)
        {
            if (fileStamp.ModifiedSeconds == fileStamp2.ModifiedSeconds)
            {
                return fileStamp.Size != fileStamp2.Size;
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

        public int Write(byte[] buffer, int value2)
        {
            if (!WriteFile(this._handle, buffer, value2, out var result, IntPtr.Zero))
            {
                throw new Win32Exception();
            }
            return result;
        }

        public int Read(byte[] buffer, int value2)
        {
            if (!ReadFile(this._handle, buffer, value2, out var result, IntPtr.Zero))
            {
                throw new Win32Exception();
            }
            return result;
        }

        public long GetSize()
        {
            if (!GetFileSizeEx(_handle, out var result))
            {
                throw new Win32Exception();
            }
            return result;
        }

        public void Dispose()
        {
            _handle.Dispose();
        }

        public void SetLastWriteTime(long value2)
        {
            if (!SetFileTime(_handle, IntPtr.Zero, IntPtr.Zero, ref value2))
            {
                throw new Win32Exception();
            }
        }

        public long GetLastWriteTime()
        {
            if (!GetFileTime(_handle, IntPtr.Zero, IntPtr.Zero, out var result))
            {
                throw new Win32Exception();
            }
            return result;
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
    public static extern IntPtr FindFirstFile(string text, out FindData findData);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern bool FindNextFile(IntPtr handle, out FindData findData);

    [DllImport("kernel32.dll")]
    public static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetFileAttributes(string text);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetFileAttributes(string text, [MarshalAs(UnmanagedType.U4)] int value2);

    [DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool CreateDirectory(string text, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectory(string text);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string text, string text2, MoveFileFlags moveFileFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile([MarshalAs(UnmanagedType.LPTStr)] string text, [MarshalAs(UnmanagedType.U4)] uint value2, [MarshalAs(UnmanagedType.U4)] FileShare fileShare, IntPtr handle, [MarshalAs(UnmanagedType.U4)] FileMode fileMode, [MarshalAs(UnmanagedType.U4)] FileAttributes fileAttributes, IntPtr handle2);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeHandle safeHandle, [In] IntPtr handle, out int value2, bool flag);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeHandle safeHandle, IntPtr handle, int value2, out int value3, IntPtr handle2);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeHandle safeHandle, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int value2, out int value3, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeHandle safeHandle, [In][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int value2, out int value3, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeHandle safeHandle, IntPtr handle, IntPtr handle2, ref long value2);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileTime(SafeHandle safeHandle, IntPtr handle, IntPtr handle2, out long value2);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle safeFileHandle, out FileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle safeFileHandle, out long value2);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteFile(string text);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool CreateHardLink(string text, string text2, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool CreateSymbolicLink(string text, string text2, int value2);

    public static void CreateSymbolicLink(string text, string text2)
    {
        if ((CreateSymbolicLink(text, text2, 2) && IsAccessible(text)) || (CreateSymbolicLink(text, text2, 0) && IsAccessible(text)))
        {
            return;
        }
        throw new Win32Exception("Failed to symlink " + text + " to " + text2);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool CopyFile(string text, string text2, [MarshalAs(UnmanagedType.I1)] bool flag);

    public static IEnumerable<FindData> EnumerateDirectory(LocalPath localPath, bool flag)
    {
        IntPtr intPtr = FindFirstFile(localPath.FullPath + "\\*.*", out var findData);
        if (intPtr == (IntPtr)(-1))
        {
            int lastWin32Error = Marshal.GetLastWin32Error();
            if (!((lastWin32Error == 3) & flag))
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
            while (FindNextFile(intPtr, out findData));
        }
        finally
        {
            FindClose(intPtr);
        }
    }

    public static long MakeLong(int value2, int value3)
    {
        return (long)(((ulong)(uint)value2 << 32) | (uint)value3);
    }

    public static long MakeLongUnsigned(uint value2, uint value3)
    {
        return (long)(((ulong)value2 << 32) | value3);
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

    public static bool IsAccessible(string text)
    {
        using SafeFileHandle safeFileHandle = CreateFile(text, 131072u, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
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

    public static FileInformation GetFileInformation(LocalPath localPath, out long value2, bool flag)
    {
        value2 = 0L;
        using SafeFileHandle safeFileHandle = CreateFile(localPath.FullPath, 2147483648u, FileShare.Read, IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
        if (safeFileHandle.IsInvalid || !GetFileInformationByHandle(safeFileHandle, out var result) || !GetFileSizeEx(safeFileHandle, out value2))
        {
            if (flag)
            {
                throw new Win32Exception();
            }
            return default;
        }
        return result;
    }

    public static void CreateFileLink(LocalPath localPath, string text, LinkOptions linkOptions, Action<RemoteWarningRecord> action)
    {
        LocalPath localPath2 = localPath.GetParent().Combine(text);
        int fileAttributes = GetFileAttributes(localPath.FullPath);
        if (fileAttributes != -1 && (fileAttributes & 0x400) != 0 && (fileAttributes & 0x10) == 0)
        {
            KeyValuePair<string, bool> keyValuePair = ReparsePoints.ReadReparsePoint(localPath.FullPath);
            if (StringComparer.InvariantCulture.Compare(keyValuePair.Key, text) == 0)
            {
                return;
            }
        }
        if (fileAttributes != -1 && (fileAttributes & 0x400) == 0)
        {
            FileInformation fileInformation = LocalFileSystem.GetFileInformation(localPath, out var num, flag: false);
            FileInformation fileInformation2 = LocalFileSystem.GetFileInformation(localPath2, out var num2, flag: false);
            if (fileInformation.Attributes != 0 && fileInformation2.Attributes != 0 && ((fileInformation.NumberOfLinks > 1 && fileInformation.VolumeSerialNumber == fileInformation2.VolumeSerialNumber && fileInformation.FileIndex == fileInformation2.FileIndex) || (fileInformation.LastWriteTime == fileInformation2.LastWriteTime && num == num2)))
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
                    action?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteFile, localPath.FullPath, Marshal.GetLastWin32Error()));
                }
            }
            else if ((fileAttributes & 0x400) == 0)
            {
                DeleteDirectoryRecursive(localPath, action);
            }
            else if (!RemoveDirectory(localPath.FullPath))
            {
                action?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteDirectory, localPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        try
        {
            CreateDirectoryRecursive(localPath.GetParent());
        }
        catch
        {
            action?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateDirectory, localPath.GetParent().FullPath, Marshal.GetLastWin32Error()));
            return;
        }
        if ((linkOptions & LinkOptions.SkipSymlinks) == 0)
        {
            try
            {
                CreateSymbolicLink(localPath.FullPath, text.Replace('/', '\\'));
                return;
            }
            catch
            {
                action?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateSymlink, localPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        if ((linkOptions & LinkOptions.SkipHardlinks) == 0)
        {
            try
            {
                if (!CreateHardLink(localPath.FullPath, localPath2.FullPath, IntPtr.Zero))
                {
                    throw new Win32Exception($"Failed to hardlink {localPath} to {localPath2}");
                }
                return;
            }
            catch
            {
                action?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateHardlink, localPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        try
        {
            if (!CopyFile(localPath.FullPath, localPath2.FullPath, flag: false))
            {
                throw new Win32Exception($"Failed to copy {localPath2} to {localPath}");
            }
        }
        catch
        {
            action?.Invoke(new RemoteWarningRecord(FileOperationFailure.CopyFile, localPath.FullPath, Marshal.GetLastWin32Error()));
        }
    }

    private static LocalPath ResolveSymlinkTarget(LocalPath localPath)
    {
        KeyValuePair<string, bool> keyValuePair = ReparsePoints.ReadReparsePoint(localPath.FullPath);
        string text = keyValuePair.Key ?? "";
        if (text.StartsWith("\\\\?\\"))
        {
            text = text.Substring(4);
        }
        if (!keyValuePair.Value || text.StartsWith("\\\\") || (text.Length >= 2 && text[1] == ':'))
        {
            return LocalPath.FromUserPath(keyValuePair.Key);
        }
        return localPath.GetParent().Combine(text);
    }

    public static LocalPath ResolveFinalSymlink(LocalPath localPath)
    {
        HashSet<string> hashSet = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        int fileAttributes;
        do
        {
            hashSet.Add(localPath.FullPath);
            localPath = ResolveSymlinkTarget(localPath);
            if (hashSet.Contains(localPath.FullPath))
            {
                throw new Exception("Recursive symlink: " + localPath.FullPath);
            }
            fileAttributes = GetFileAttributes(localPath.FullPath);
        }
        while (fileAttributes != -1 && (fileAttributes & 0x400) != 0);
        return localPath;
    }

    public static void MoveFile(LocalPath localPath, LocalPath localPath2, MoveFileFlags moveFileFlags = MoveFileFlags.None)
    {
        if (!MoveFileEx(localPath.FullPath, localPath2.FullPath, moveFileFlags))
        {
            throw new Win32Exception();
        }
    }

    public static void DeleteDirectoryRecursive(LocalPath localPath, Action<RemoteWarningRecord> action)
    {
        foreach (FindData item in EnumerateDirectory(localPath, flag: false))
        {
            LocalPath localPath2 = localPath.Combine(item.FileName);
            if (item.HasAttribute(FileAttributes.Directory))
            {
                if (!item.HasAttribute(FileAttributes.ReparsePoint))
                {
                    DeleteDirectoryRecursive(localPath2, action);
                }
                else if (!RemoveDirectory(localPath2.FullPath))
                {
                    action?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteDirectory, localPath2.FullPath, Marshal.GetLastWin32Error()));
                }
            }
            else if (!TryDeleteFile(localPath2, flag: true, flag2: false))
            {
                action?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteFile, localPath2.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        if (!RemoveDirectory(localPath.FullPath))
        {
            action?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteDirectory, localPath.FullPath, Marshal.GetLastWin32Error()));
        }
    }

    private static bool TryDeleteFile(LocalPath localPath, bool flag, bool flag2)
    {
        if (DeleteFile(localPath.FullPath))
        {
            return true;
        }
        int lastWin32Error = Marshal.GetLastWin32Error();
        if ((lastWin32Error == 2) & flag2)
        {
            return true;
        }
        if ((lastWin32Error == 5) & flag)
        {
            int fileAttributes = GetFileAttributes(localPath.FullPath);
            if (fileAttributes != -1 && (fileAttributes & 1) != 0)
            {
                SetFileAttributes(localPath.FullPath, fileAttributes & -2);
                if (DeleteFile(localPath.FullPath))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static void DeleteFile(LocalPath localPath, bool flag, bool flag2 = false)
    {
        if (TryDeleteFile(localPath, flag, flag2))
        {
            return;
        }
        LocalPath localPath2 = localPath;
        throw new Win32Exception("Failed to delete " + localPath2.ToString());
    }

    public static void DeleteDirectory(LocalPath localPath)
    {
        if (RemoveDirectory(localPath.FullPath))
        {
            return;
        }
        if (Marshal.GetLastWin32Error() == 5)
        {
            int fileAttributes = GetFileAttributes(localPath.FullPath);
            if (fileAttributes != -1 && (fileAttributes & 1) != 0)
            {
                SetFileAttributes(localPath.FullPath, fileAttributes & -2);
                if (RemoveDirectory(localPath.FullPath))
                {
                    return;
                }
            }
        }
        LocalPath localPath2 = localPath;
        throw new Win32Exception("Failed to delete " + localPath2.ToString());
    }

    public static void CreateDirectoryLink(LocalPath localPath, string text2, LinkOptions linkOptions, Action<RemoteWarningRecord> action)
    {
        int fileAttributes = GetFileAttributes(localPath.FullPath);
        if (fileAttributes != -1 && (fileAttributes & 0x400) != 0 && (fileAttributes & 0x10) != 0)
        {
            KeyValuePair<string, bool> keyValuePair = ReparsePoints.ReadReparsePoint(localPath.FullPath);
            if (StringComparer.InvariantCulture.Compare(keyValuePair.Key, text2) == 0)
            {
                return;
            }
        }
        if (fileAttributes != -1 && (fileAttributes & 0x10) == 0 && !DeleteFile(localPath.FullPath))
        {
            action?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteFile, localPath.FullPath, Marshal.GetLastWin32Error()));
        }
        try
        {
            CreateDirectoryRecursive(localPath);
        }
        catch
        {
            action?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateDirectory, localPath.GetParent().FullPath, Marshal.GetLastWin32Error()));
            return;
        }
        if ((linkOptions & LinkOptions.SkipSymlinks) == 0)
        {
            try
            {
                ReparsePoints.CreateSymbolicLink(localPath.FullPath, text2.Replace('/', '\\'), flag: true);
                return;
            }
            catch
            {
                action?.Invoke(new RemoteWarningRecord(FileOperationFailure.CreateSymlink, localPath.FullPath, Marshal.GetLastWin32Error()));
            }
        }
        if (text2.Contains("."))
        {
            LocalPath localPath2 = localPath.Combine("..\\" + text2.Replace('/', '\\'));
            if (localPath.FullPath.ToLower() == localPath2.FullPath.ToLower() || localPath.FullPath.StartsWith(localPath2.FullPath.TrimEnd('\\') + "\\", StringComparison.InvariantCultureIgnoreCase))
            {
                action?.Invoke(new RemoteWarningRecord(FileOperationFailure.RecursiveLink, localPath.FullPath, Marshal.GetLastWin32Error()));
                return;
            }
        }
        Dictionary<string, FindData> dictionary = EnumerateDirectory(localPath, flag: true).ToOverridingDictionary((FindData findData) => findData.FileName, StringComparer.InvariantCultureIgnoreCase);
        foreach (FindData item in EnumerateDirectory(localPath.GetParent().Combine(text2), flag: true))
        {
            dictionary.Remove(item.FileName);
            string text = "..\\" + text2 + "\\" + item.FileName;
            if (item.HasAttribute(FileAttributes.Directory))
            {
                CreateDirectoryLink(localPath.Combine(item.FileName), text, linkOptions | LinkOptions.SkipSymlinks, action);
            }
            else
            {
                CreateFileLink(localPath.Combine(item.FileName), text, linkOptions | LinkOptions.SkipSymlinks, action);
            }
        }
        if ((linkOptions & LinkOptions.DeleteExtraneous) != LinkOptions.DeleteExtraneous)
        {
            return;
        }
        foreach (FindData value in dictionary.Values)
        {
            LocalPath localPath3 = localPath.Combine(value.FileName);
            if (!value.HasAttribute(FileAttributes.Directory))
            {
                if (!DeleteFile(localPath3.FullPath))
                {
                    action?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteFile, localPath3.FullPath, Marshal.GetLastWin32Error()));
                }
            }
            else if (!value.HasAttribute(FileAttributes.ReparsePoint))
            {
                DeleteDirectoryRecursive(localPath3, action);
            }
            else if (!RemoveDirectory(localPath3.FullPath))
            {
                action?.Invoke(new RemoteWarningRecord(FileOperationFailure.DeleteDirectory, localPath3.FullPath, Marshal.GetLastWin32Error()));
            }
        }
    }
}
