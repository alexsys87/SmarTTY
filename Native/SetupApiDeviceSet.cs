using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SmarTTY.Native;

public class SetupApiDeviceSet : IDisposable
{
    public struct ComPortInfo
    {
        public int PortNumber;

        public string Description;

        public string InstanceId;
    }

    public struct SpDevInfoData
    {
        public int Size;

        public Guid ClassGuid;

        public uint DevInst;

        public IntPtr Reserved;
    }

    private struct SpClassInstallHeader
    {
        public int Size;

        public DeviceInstallFunction InstallFunction;
    }

    private struct SpPropChangeParams
    {
        public SpClassInstallHeader Header;

        public int StateChange;

        public int Scope;

        public int HwProfile;
    }

    public struct SpDeviceInterfaceData
    {
        public int Size;

        public Guid InterfaceClassGuid;

        public int Flags;

        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SpDeviceInterfaceDetailData
    {
        public uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string DevicePath;
    }

    [Flags]
    private enum DiGetClassFlags : uint
    {
        Default = 1u,
        Present = 2u,
        AllClasses = 4u,
        Profile = 8u,
        DeviceInterface = 0x10u
    }

    private enum DeviceInstallFunction
    {
        InstallDevice = 1,
        AssignResources = 2,
        Properties = 3,
        Remove = 4,
        FirstTimeSetup = 5,
        Unremove = 6,
        SelectDevice = 7,
        EnumerateFunctions = 8,
        RegisterDevice = 9,
        InstallInterfaces = 10,
        Detect = 11,
        InstallClass = 12,
        SelectClassDrivers = 13,
        ValidateClassDrivers = 14,
        InstallClassDrivers = 15,
        CalcDiskSpace = 16,
        DestroyPrivateData = 17,
        ValidateDriver = 18,
        DetectUnused19 = 19,
        Movedevice = 20,
        SelectBestCompatDrv = 21,
        AllowInstall = 22,
        RegisterCoinstallers = 23,
        PropertyChange = 24,
        EnableClass = 25,
        DetectUnused26 = 26,
        DetectUnused27 = 27,
        DetectUnused28 = 28,
        DetectUnused29 = 29,
        DetectUnused30 = 30,
        DetectUnused31 = 31,
        DetectUnused32 = 32,
        DetectUnused33 = 33,
        DetectUnused34 = 34,
        DetectUnused35 = 35,
        DetectUnused36 = 36,
        DetectUnused37 = 37,
        DetectUnused38 = 38,
        DetectUnused39 = 39,
        DetectUnused40 = 40,
        DetectUnused41 = 41,
        DetectUnused48 = 48
    }

    private enum DeviceRegistryProperty
    {
        DeviceDescription = 0,
        HardwareId = 1,
        Driver = 9,
        FriendlyName = 12
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct SpDriverInfoData
    {
        public int Size;

        public int DriverType;

        private IntPtr _reserved;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ManufacturerName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProviderName;

        public System.Runtime.InteropServices.ComTypes.FILETIME DriverDate;

        public long DriverVersion;
    }

    public enum OemSourceMediaType
    {
        None,
        Path,
        Url,
        Max
    }

    public enum SetupCopyStyle
    {
        None = 0,
        WarnIfSkip = 1,
        NoOverwrite = 2,
        NoDecompress = 4,
        ForceNewer = 8,
        NoBackup = 16,
        CheckSecurityByFile = 32,
        NewerOnly = 64,
        NoBrowse = 128,
        CopyAlways = 256,
        NoCat = 512,
        VerifySqm = 1024,
        InUse = 2048,
        InUseSubst = 4096,
        RenameToBackup = 8192,
        ForceNoPath = 16384,
        SecurityEx = 32768,
        SubDir = 65536,
        TargetReadonly = 131072,
        OverwriteFileInUse = 262144,
        TargetPruned = 524288,
        NoVerify = 1048576
    }

    public enum UninstallOemInfFlags : uint
    {
        None,
        ForceDelete
    }

    private IntPtr _deviceInfoSet;

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, DeviceRegistryProperty property, out uint propertyRegDataType, StringBuilder propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, [MarshalAs(UnmanagedType.LPTStr)] string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, [MarshalAs(UnmanagedType.LPTStr)] string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, IntPtr interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, ref SpDeviceInterfaceDetailData deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, IntPtr deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern int CM_Get_Device_ID(IntPtr devNode, StringBuilder buffer, int bufferLen, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, ref SpPropChangeParams classInstallParams, int classInstallParamsSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfoW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, int driverType, int memberIndex, ref SpDriverInfoData driverInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, int driverType);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetSelectedDevice(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetSelectedDriver(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, ref SpDriverInfoData driverInfoData);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InstallSelectedDriver(IntPtr hwndParent, IntPtr deviceInfoSet, IntPtr reserved, bool backup, out int rebootRequired);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupCopyOEMInf(string sourceInfFileName, string oemInfFileName, OemSourceMediaType oemSourceMediaType, SetupCopyStyle copyStyle, string destinationInfFileName, int destinationInfFileNameSize, IntPtr requiredSize, string destinationInfFileNameComponent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupUninstallOEMInf(string infFileName, UninstallOemInfFlags flags, IntPtr reserved);

    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    public SetupApiDeviceSet(Guid guid)
    {
        _deviceInfoSet = SetupDiGetClassDevs(ref guid, null, (IntPtr)0, (uint)DiGetClassFlags.Present);
        if (_deviceInfoSet == InvalidHandleValue)
        {
            // Nothing to enumerate; an invalid set would make every call fail.
            _deviceInfoSet = IntPtr.Zero;
        }
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern int CM_Locate_DevNode(out int devNode, string deviceId, int flags);

    [DllImport("setupapi.dll")]
    private static extern int CM_Get_Parent(out int parentDevNode, int devNode, int flags);

    public static string GetParentDeviceId(string deviceId)
    {
        if (CM_Locate_DevNode(out var devNode, deviceId, 0) != 0)
        {
            return null;
        }
        if (CM_Get_Parent(out var parentDevNode, devNode, 0) != 0)
        {
            return null;
        }
        StringBuilder stringBuilder = new StringBuilder
        {
            Capacity = 1024
        };
        CM_Get_Device_ID((IntPtr)parentDevNode, stringBuilder, stringBuilder.Capacity - 1, 0);
        return stringBuilder.ToString().TrimEnd(default(char));
    }

    public static string GetUsbSerialNumber(string deviceId, out bool isUsbDevice)
    {
        string currentDeviceId = deviceId;
        if (currentDeviceId != null && !currentDeviceId.StartsWith("USB\\"))
        {
            currentDeviceId = GetParentDeviceId(currentDeviceId);
        }
        if (currentDeviceId != null && currentDeviceId.StartsWith("USB\\"))
        {
            isUsbDevice = true;
        }
        else
        {
            isUsbDevice = false;
        }
        while (currentDeviceId != null && currentDeviceId.Contains("&MI_"))
        {
            currentDeviceId = GetParentDeviceId(currentDeviceId);
        }
        string text = null;
        int slashIndex = currentDeviceId?.LastIndexOf('\\') ?? (-1);
        if (slashIndex > 0)
        {
            text = currentDeviceId.Substring(slashIndex + 1);
        }
        return text;
    }

    public IEnumerable<ComPortInfo> EnumerateComPorts()
    {
        SpDevInfoData spDevInfoData = default;
        uint memberIndex = 0u;
        while (_deviceInfoSet != IntPtr.Zero)
        {
            spDevInfoData.Size = Marshal.SizeOf((object)spDevInfoData);
            if (!SetupDiEnumDeviceInfo(_deviceInfoSet, memberIndex, ref spDevInfoData))
            {
                // ERROR_NO_MORE_ITEMS ends the list; any other error would repeat for every index and never end it.
                break;
            }
            else
            {
                int requiredSize = 0;
                SetupDiGetDeviceInstanceId(_deviceInfoSet, ref spDevInfoData, IntPtr.Zero, 0, out requiredSize);
                StringBuilder stringBuilder = new StringBuilder(requiredSize);
                if (SetupDiGetDeviceInstanceId(_deviceInfoSet, ref spDevInfoData, stringBuilder, requiredSize, out requiredSize))
                {
                    string friendlyName = GetRegistryProperty(_deviceInfoSet, ref spDevInfoData, DeviceRegistryProperty.FriendlyName);
                    if (friendlyName != null)
                    {
                        string description = GetRegistryProperty(_deviceInfoSet, ref spDevInfoData, DeviceRegistryProperty.DeviceDescription);
                        int comIndex = friendlyName.LastIndexOf("(COM");
                        if (comIndex != -1)
                        {
                            int closingParenIndex = friendlyName.IndexOf(')', comIndex);
                            if (closingParenIndex != -1 && int.TryParse(friendlyName.Substring(comIndex + 4, closingParenIndex - comIndex - 4), out var portNumber))
                            {
                                yield return new ComPortInfo
                                {
                                    Description = description,
                                    PortNumber = portNumber,
                                    InstanceId = stringBuilder.ToString()
                                };
                            }
                        }
                    }
                }
            }
            memberIndex++;
        }
    }

    private string GetRegistryProperty(IntPtr handle, ref SpDevInfoData spDevInfoData, DeviceRegistryProperty deviceRegistryProperty)
    {
        SetupDiGetDeviceRegistryProperty(handle, ref spDevInfoData, deviceRegistryProperty, out var dataType, null, 0u, out var requiredSize);
        StringBuilder stringBuilder = new StringBuilder((int)requiredSize);
        if (!SetupDiGetDeviceRegistryProperty(handle, ref spDevInfoData, deviceRegistryProperty, out dataType, stringBuilder, requiredSize, out requiredSize))
        {
            return null;
        }
        return stringBuilder.ToString();
    }

    public void Dispose()
    {
        if (_deviceInfoSet != IntPtr.Zero)
        {
            SetupDiDestroyDeviceInfoList(_deviceInfoSet);
            _deviceInfoSet = IntPtr.Zero;
        }
    }
}
