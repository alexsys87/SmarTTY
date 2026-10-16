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
    private static extern bool DeviceIoControl(SafeFileHandle safeFileHandle, uint value2, ref MountPointReparseBuffer mountPointReparseBuffer, int value3, IntPtr handle, int value4, out int value5, IntPtr handle2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle safeFileHandle, uint value2, ref SymbolicLinkReparseBuffer symbolicLinkReparseBuffer, int value3, IntPtr handle, int value4, out int value5, IntPtr handle2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle safeFileHandle, uint value2, IntPtr handle, int value3, [Out][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] byte[] buffer, int value4, out int value5, IntPtr handle2);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string text, FileAccessRights fileAccessRights, FileShareMode fileShareMode, IntPtr handle, CreationDisposition creationDisposition, FileFlags fileFlags, IntPtr handle2);

    [DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern bool CreateDirectory(string text, IntPtr handle);

    public static void CreateJunction(string text, string text2)
    {
        if (!Directory.Exists(text2))
        {
            throw new Exception(text2 + " does not exist.");
        }
        text2 = Path.GetFullPath(text2);
        Directory.CreateDirectory(text);
        using SafeFileHandle safeFileHandle = CreateFile(text, ReparsePoints.FileAccessRights.GenericWrite, FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete, IntPtr.Zero, CreationDisposition.OpenExisting, FileFlags.BackupSemantics | FileFlags.OpenReparsePoint, IntPtr.Zero);
        if (safeFileHandle.IsInvalid)
        {
            throw new Win32Exception("Failed to open " + text);
        }
        ushort num = (ushort)(8 + text2.Length * 2);
        MountPointReparseBuffer mountPointReparseBuffer = new MountPointReparseBuffer
        {
            PathBuffer = "\\??\\" + text2,
            ReparseTag = 2684354563u,
            ReparseDataLength = (ushort)(num + 12),
            SubstituteNameOffset = 0,
            SubstituteNameLength = num,
            PrintNameOffset = (ushort)(num + 2),
            PrintNameLength = 0
        };
        if (!DeviceIoControl(safeFileHandle, 589988u, ref mountPointReparseBuffer, num + 20, IntPtr.Zero, 0, out var _, IntPtr.Zero))
        {
            throw new Win32Exception("FSCTL_SET_REPARSE_POINT failed");
        }
    }

    public static void CreateSymbolicLink(string text, string text2, bool flag)
    {
        if (flag)
        {
            CreateDirectory(text, IntPtr.Zero);
        }
        try
        {
            using SafeFileHandle safeFileHandle = CreateFile(text, ReparsePoints.FileAccessRights.GenericWrite, FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete, IntPtr.Zero, flag ? ReparsePoints.CreationDisposition.OpenExisting : ReparsePoints.CreationDisposition.CreateAlways, FileFlags.BackupSemantics | FileFlags.OpenReparsePoint, IntPtr.Zero);
            if (safeFileHandle.IsInvalid)
            {
                throw new Win32Exception("Failed to open " + text);
            }
            ushort num = (ushort)(text2.Length * 2);
            SymbolicLinkReparseBuffer symbolicLinkReparseBuffer = new SymbolicLinkReparseBuffer
            {
                PathBuffer = text2 + text2,
                ReparseTag = 2684354572u,
                ReparseDataLength = (ushort)(12 + num * 2),
                SubstituteNameOffset = num,
                SubstituteNameLength = num,
                PrintNameOffset = 0,
                PrintNameLength = num,
                Flags = 1u
            };
            if (!DeviceIoControl(safeFileHandle, 589988u, ref symbolicLinkReparseBuffer, num * 2 + 20, IntPtr.Zero, 0, out var _, IntPtr.Zero))
            {
                throw new Win32Exception("FSCTL_SET_REPARSE_POINT failed");
            }
        }
        catch
        {
            if (!flag)
            {
                LocalFileSystem.DeleteFile(LocalPath.FromRaw(text), flag: true);
            }
            throw;
        }
    }

    public static KeyValuePair<string, bool> ReadReparsePoint(string text3)
    {
        using SafeFileHandle safeFileHandle = CreateFile(text3, FileAccessRights.GenericRead, FileShareMode.Read | FileShareMode.Write | FileShareMode.Delete, IntPtr.Zero, CreationDisposition.OpenExisting, FileFlags.BackupSemantics | FileFlags.OpenReparsePoint, IntPtr.Zero);
        if (safeFileHandle.IsInvalid)
        {
            throw new Win32Exception();
        }
        byte[] array = new byte[65536];
        if (!DeviceIoControl(safeFileHandle, 589992u, IntPtr.Zero, 0, array, array.Length, out var _, IntPtr.Zero))
        {
            throw new Win32Exception();
        }
        uint num2 = BitConverter.ToUInt32(array, 0);
        switch (num2)
        {
            case 2684354572u:
                {
                    int num5 = 20;
                    int num6 = BitConverter.ToInt16(array, 8);
                    int count2 = BitConverter.ToInt16(array, 10);
                    int num7 = BitConverter.ToInt32(array, 16);
                    string text2 = Encoding.Unicode.GetString(array, num5 + num6, count2);
                    if (text2.StartsWith("\\??\\"))
                    {
                        text2 = text2.Substring(4);
                    }
                    return new KeyValuePair<string, bool>(text2, (num7 & 1) != 0);
                }
            case 2684354563u:
                {
                    int num3 = 16;
                    int num4 = BitConverter.ToInt16(array, 8);
                    int count = BitConverter.ToInt16(array, 10);
                    string text = Encoding.Unicode.GetString(array, num3 + num4, count);
                    if (text.StartsWith("\\??\\"))
                    {
                        text = text.Substring(4);
                    }
                    return new KeyValuePair<string, bool>(text, value: false);
                }
            default:
                throw new Exception($"Unknown reparse tag: 0x{num2:x8}");
        }
    }
}
