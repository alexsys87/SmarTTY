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
        Value1 = 1,
        Value2 = 2,
        Value3 = 3,
        Value4 = 4,
        Value5 = 5,
        Value6 = 6,
        Value7 = 7,
        Value8 = 8,
        Value9 = 9,
        Value10 = 10,
        Value11 = 11,
        Value12 = 12,
        Value13 = 13,
        Value14 = 14,
        Value15 = 15,
        Value16 = 16,
        Value17 = 17,
        Value18 = 18,
        Value19 = 19,
        Value20 = 20,
        Value21 = 21,
        Value22 = 22,
        Value23 = 23,
        Value24 = 24,
        Value25 = 25,
        Value26 = 26,
        Value27 = 27,
        Value28 = 28,
        Value29 = 29,
        Value30 = 30,
        Value31 = 31,
        Value32 = 32,
        Value33 = 33,
        Value34 = 34,
        Value35 = 35,
        Value36 = 36,
        Value37 = 37,
        Value38 = 38,
        Value39 = 39,
        Value40 = 40,
        Value41 = 41,
        Value48 = 48
    }

    public enum OemSourceMediaTypeLegacy
    {
        Value1 = 1,
        Value2,
        Value3,
        Value4,
        Value5
    }

    [Flags]
    private enum DeviceFlagsLegacy
    {
        Flag0x1 = 1,
        Flag0x2 = 2,
        Flag0x4 = 4
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
        Value0 = 0,
        Value1 = 1,
        Value2 = 2,
        Value4 = 4,
        Value4Alias = Value4,
        Value8 = 8,
        Value16 = 16,
        Value32 = 32,
        Value64 = 64,
        Value128 = 128,
        Value256 = 256,
        Value512 = 512,
        Value1024 = 1024,
        Value2048 = 2048,
        Value4096 = 4096,
        Value8192 = 8192,
        Value16384 = 16384,
        SetupCopyStyle64 = 32768,
        Value65536 = 65536,
        Value131072 = 131072,
        Value262144 = 262144,
        Value524288 = 524288,
        Value1048576 = 1048576
    }

    public enum UninstallOemInfFlags : uint
    {
        None,
        ForceDelete
    }

    private IntPtr _deviceInfoSet;

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr handle, ref SpDevInfoData spDevInfoData, DeviceRegistryProperty deviceRegistryProperty, out uint value2, StringBuilder stringBuilder, uint value3, out uint value4);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, [MarshalAs(UnmanagedType.LPTStr)] string text, IntPtr handle, uint value2);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr handle, [MarshalAs(UnmanagedType.LPTStr)] string text, IntPtr handle2, uint value2);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr handle, IntPtr handle2, IntPtr handle3, uint value2, ref SpDeviceInterfaceData spDeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr handle, ref SpDeviceInterfaceData spDeviceInterfaceData, ref SpDeviceInterfaceDetailData spDeviceInterfaceDetailData, uint value2, out uint value3, ref SpDevInfoData spDevInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr handle, ref SpDeviceInterfaceData spDeviceInterfaceData, IntPtr handle2, uint value2, out uint value3, IntPtr handle3);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr handle, ref SpDevInfoData spDevInfoData, StringBuilder stringBuilder, int value2, out int value3);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr handle, ref SpDevInfoData spDevInfoData, IntPtr handle2, int value2, out int value3);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr handle, uint value2, ref SpDevInfoData spDevInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern int CM_Get_Device_ID(IntPtr handle, StringBuilder stringBuilder, int value2, int value3);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr handle, ref SpDevInfoData spDevInfoData, ref SpPropChangeParams spPropChangeParams, int value2);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint value2, IntPtr handle, ref SpDevInfoData spDevInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfoW(IntPtr handle, ref SpDevInfoData spDevInfoData, int value2, int value3, ref SpDriverInfoData spDriverInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(IntPtr handle, ref SpDevInfoData spDevInfoData, int value2);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetSelectedDevice(IntPtr handle, ref SpDevInfoData spDevInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetSelectedDriver(IntPtr handle, ref SpDevInfoData spDevInfoData, ref SpDriverInfoData spDriverInfoData);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InstallSelectedDriver(IntPtr handle, IntPtr handle2, IntPtr handle3, bool flag, out int value2);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupCopyOEMInf(string text, string text2, OemSourceMediaType oemSourceMediaType, SetupCopyStyle setupCopyStyle, string text3, int value2, IntPtr handle, string text4);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupUninstallOEMInf(string text, UninstallOemInfFlags uninstallOemInfFlags, IntPtr handle);

    public SetupApiDeviceSet(Guid guid)
    {
        _deviceInfoSet = SetupDiGetClassDevs(ref guid, null, (IntPtr)0, 2u);
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern int CM_Locate_DevNode(out int value2, string text, int value3);

    [DllImport("setupapi.dll")]
    private static extern int CM_Get_Parent(out int value2, int value3, int value4);

    public static string GetParentDeviceId(string text)
    {
        if (CM_Locate_DevNode(out var num, text, 0) != 0)
        {
            return null;
        }
        if (CM_Get_Parent(out var num2, num, 0) != 0)
        {
            return null;
        }
        StringBuilder stringBuilder = new StringBuilder
        {
            Capacity = 1024
        };
        CM_Get_Device_ID((IntPtr)num2, stringBuilder, stringBuilder.Capacity - 1, 0);
        return stringBuilder.ToString().TrimEnd(default(char));
    }

    public static string GetUsbSerialNumber(string text2, out bool flag)
    {
        string text = text2;
        if (!text.StartsWith("USB\\"))
        {
            text = GetParentDeviceId(text);
        }
        if (text.StartsWith("USB\\"))
        {
            flag = true;
        }
        else
        {
            flag = false;
        }
        while (text != null && text.Contains("&MI_"))
        {
            text = GetParentDeviceId(text);
        }
        string result = null;
        int num = text?.LastIndexOf('\\') ?? (-1);
        if (num > 0)
        {
            result = text.Substring(num + 1);
        }
        return result;
    }

    public IEnumerable<ComPortInfo> EnumerateComPorts()
    {
        SpDevInfoData spDevInfoData = default;
        uint num = 0u;
        while (true)
        {
            spDevInfoData.Size = Marshal.SizeOf((object)spDevInfoData);
            if (!SetupDiEnumDeviceInfo(_deviceInfoSet, num, ref spDevInfoData))
            {
                if (Marshal.GetLastWin32Error() == 259)
                {
                    break;
                }
            }
            else
            {
                int capacity = 0;
                SetupDiGetDeviceInstanceId(_deviceInfoSet, ref spDevInfoData, IntPtr.Zero, 0, out capacity);
                StringBuilder stringBuilder = new StringBuilder(capacity);
                if (SetupDiGetDeviceInstanceId(_deviceInfoSet, ref spDevInfoData, stringBuilder, capacity, out capacity))
                {
                    string text = GetRegistryProperty(_deviceInfoSet, ref spDevInfoData, DeviceRegistryProperty.FriendlyName);
                    if (text != null)
                    {
                        string text2 = GetRegistryProperty(_deviceInfoSet, ref spDevInfoData, DeviceRegistryProperty.DeviceDescription);
                        int num2 = text.LastIndexOf("(COM");
                        if (num2 != -1)
                        {
                            int num3 = text.IndexOf(')', num2);
                            if (num3 != -1 && int.TryParse(text.Substring(num2 + 4, num3 - num2 - 4), out var result))
                            {
                                yield return new ComPortInfo
                                {
                                    Description = text2,
                                    PortNumber = result,
                                    InstanceId = stringBuilder.ToString()
                                };
                            }
                        }
                    }
                }
            }
            num++;
        }
    }

    private string GetRegistryProperty(IntPtr handle, ref SpDevInfoData spDevInfoData, DeviceRegistryProperty deviceRegistryProperty)
    {
        SetupDiGetDeviceRegistryProperty(handle, ref spDevInfoData, deviceRegistryProperty, out var num, null, 0u, out var capacity);
        StringBuilder stringBuilder = new StringBuilder((int)capacity);
        if (!SetupDiGetDeviceRegistryProperty(handle, ref spDevInfoData, deviceRegistryProperty, out num, stringBuilder, capacity, out capacity))
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
        }
    }
}
