using Microsoft.Win32.SafeHandles;
using SmarTTY.Sync;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SmarTTY.Native;

public static class ReparsePoints
{
    [Flags]
    private enum FileAccessRights : uint
    {
        GenericRead = 0x80000000u,
        GenericWrite = 0x40000000u,
        GenericExecute = 0x20000000u,
        GenericAll = 0x10000000u
    }

    [Flags]
    private enum FileShareMode : uint
    {
        None = 0u,
        Read = 1u,
        Write = 2u,
        Delete = 4u
    }

    private enum CreationDisposition : uint
    {
        CreateNew = 1u,
        CreateAlways,
        OpenExisting,
        OpenAlways,
        TruncateExisting
    }

    [Flags]
    private enum FileFlags : uint
    {
        ReadOnly = 1u,
        Hidden = 2u,
        System = 4u,
        Directory = 0x10u,
        Archive = 0x20u,
        Device = 0x40u,
        Normal = 0x80u,
        Temporary = 0x100u,
        SparseFile = 0x200u,
        ReparsePoint = 0x400u,
        Compressed = 0x800u,
        Offline = 0x1000u,
        NotContentIndexed = 0x2000u,
        Encrypted = 0x4000u,
        WriteThrough = 0x80000000u,
        Overlapped = 0x40000000u,
        NoBuffering = 0x20000000u,
        RandomAccess = 0x10000000u,
        SequentialScan = 0x8000000u,
        DeleteOnClose = 0x4000000u,
        BackupSemantics = 0x2000000u,
        PosixSemantics = 0x1000000u,
        OpenReparsePoint = 0x200000u,
        OpenNoRecall = 0x100000u,
        FirstPipeInstance = 0x80000u
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MountPointReparseBuffer
    {
        public uint ReparseTag;

        public ushort ReparseDataLength;

        public ushort Reserved;

        public ushort SubstituteNameOffset;

        public ushort SubstituteNameLength;

        public ushort PrintNameOffset;

        public ushort PrintNameLength;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16384)]
        public string PathBuffer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SymbolicLinkReparseBuffer
    {
        public uint ReparseTag;

        public ushort ReparseDataLength;

        public ushort Reserved;

        public ushort SubstituteNameOffset;

        public ushort SubstituteNameLength;

        public ushort PrintNameOffset;

        public ushort PrintNameLength;

        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16384)]
        public string PathBuffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle safeFileHandle, uint ioControlCode, ref MountPointReparseBuffer inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle safeFileHandle, uint ioControlCode, ref SymbolicLinkReparseBuffer inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle safeFileHandle, uint ioControlCode, IntPtr inBuffer, int inBufferSize, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, FileAccessRights fileAccessRights, FileShareMode fileShareMode, IntPtr securityAttributes, CreationDisposition creationDisposition, FileFlags fileFlags, IntPtr templateFile);

    [DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    public static void CreateJunction(string linkPath, string targetPath)
    {
        if (!Directory.Exists(targetPath))
        {
            throw new Exception(targetPath + " does not exist.");
        }
        targetPath = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(linkPath);
        using SafeFileHandle safeFileHandle = CreateFile(linkPath, ReparsePoints.FileAccessRights.GenericWrite, FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete, IntPtr.Zero, CreationDisposition.OpenExisting, FileFlags.BackupSemantics | FileFlags.OpenReparsePoint, IntPtr.Zero);
        if (safeFileHandle.IsInvalid)
        {
            throw new Win32Exception("Failed to open " + linkPath);
        }
        ushort substituteLength = (ushort)(8 + targetPath.Length * 2);
        MountPointReparseBuffer mountPointReparseBuffer = new MountPointReparseBuffer
        {
            PathBuffer = "\\??\\" + targetPath,
            ReparseTag = 2684354563u,
            ReparseDataLength = (ushort)(substituteLength + 12),
            SubstituteNameOffset = 0,
            SubstituteNameLength = substituteLength,
            PrintNameOffset = (ushort)(substituteLength + 2),
            PrintNameLength = 0
        };
        if (!DeviceIoControl(safeFileHandle, 589988u, ref mountPointReparseBuffer, substituteLength + 20, IntPtr.Zero, 0, out var bytesReturned, IntPtr.Zero))
        {
            throw new Win32Exception("FSCTL_SET_REPARSE_POINT failed");
        }
    }

    public static void CreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        if (isDirectory)
        {
            CreateDirectory(linkPath, IntPtr.Zero);
        }
        try
        {
            using SafeFileHandle safeFileHandle = CreateFile(linkPath, ReparsePoints.FileAccessRights.GenericWrite, FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete, IntPtr.Zero, isDirectory ? ReparsePoints.CreationDisposition.OpenExisting : ReparsePoints.CreationDisposition.CreateAlways, FileFlags.BackupSemantics | FileFlags.OpenReparsePoint, IntPtr.Zero);
            if (safeFileHandle.IsInvalid)
            {
                throw new Win32Exception("Failed to open " + linkPath);
            }
            ushort targetNameLength = (ushort)(targetPath.Length * 2);
            SymbolicLinkReparseBuffer symbolicLinkReparseBuffer = new SymbolicLinkReparseBuffer
            {
                PathBuffer = targetPath + targetPath,
                ReparseTag = 2684354572u,
                ReparseDataLength = (ushort)(12 + targetNameLength * 2),
                SubstituteNameOffset = targetNameLength,
                SubstituteNameLength = targetNameLength,
                PrintNameOffset = 0,
                PrintNameLength = targetNameLength,
                Flags = 1u
            };
            if (!DeviceIoControl(safeFileHandle, 589988u, ref symbolicLinkReparseBuffer, targetNameLength * 2 + 20, IntPtr.Zero, 0, out var bytesReturned, IntPtr.Zero))
            {
                throw new Win32Exception("FSCTL_SET_REPARSE_POINT failed");
            }
        }
        catch
        {
            if (!isDirectory)
            {
                LocalFileSystem.DeleteFile(LocalPath.FromRaw(linkPath), flag: true);
            }
            throw;
        }
    }

    public static KeyValuePair<string, bool> ReadReparsePoint(string path)
    {
        using SafeFileHandle safeFileHandle = CreateFile(path, FileAccessRights.GenericRead, FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete, IntPtr.Zero, CreationDisposition.OpenExisting, FileFlags.BackupSemantics | FileFlags.OpenReparsePoint, IntPtr.Zero);
        if (safeFileHandle.IsInvalid)
        {
            throw new Win32Exception();
        }
        byte[] reparseBuffer = new byte[65536];
        if (!DeviceIoControl(safeFileHandle, 589992u, IntPtr.Zero, 0, reparseBuffer, reparseBuffer.Length, out var bytesReturned, IntPtr.Zero))
        {
            throw new Win32Exception();
        }
        uint reparseTag = BitConverter.ToUInt32(reparseBuffer, 0);
        switch (reparseTag)
        {
            case 2684354572u:
                {
                    int pathBufferOffset = 20;
                    int subNameOffset = BitConverter.ToInt16(reparseBuffer, 8);
                    int subNameLength = BitConverter.ToInt16(reparseBuffer, 10);
                    int flags = BitConverter.ToInt32(reparseBuffer, 16);
                    string printName = Encoding.Unicode.GetString(reparseBuffer, pathBufferOffset + subNameOffset, subNameLength);
                    if (printName.StartsWith("\\??\\"))
                    {
                        printName = printName.Substring(4);
                    }
                    return new KeyValuePair<string, bool>(printName, (flags & 1) != 0);
                }
            case 2684354563u:
                {
                    int pathBufferOffset = 16;
                    int subNameOffset = BitConverter.ToInt16(reparseBuffer, 8);
                    int subNameLength = BitConverter.ToInt16(reparseBuffer, 10);
                    string printName = Encoding.Unicode.GetString(reparseBuffer, pathBufferOffset + subNameOffset, subNameLength);
                    if (printName.StartsWith("\\??\\"))
                    {
                        printName = printName.Substring(4);
                    }
                    return new KeyValuePair<string, bool>(printName, value: false);
                }
            default:
                throw new Exception($"Unknown reparse tag: 0x{reparseTag:x8}");
        }
    }
}
