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

    [Flags]
    private enum DiGetClassFlags : uint
    {
        Default = 1u,
        Present = 2u,
        AllClasses = 4u,
        Profile = 8u,
        DeviceInterface = 0x10u
    }

    private enum DeviceRegistryProperty
    {
        DeviceDescription = 0,
        HardwareId = 1,
        Driver = 9,
        FriendlyName = 12
    }

    private IntPtr _deviceInfoSet;

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, DeviceRegistryProperty property, out uint propertyRegDataType, StringBuilder propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, [MarshalAs(UnmanagedType.LPTStr)] string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, IntPtr deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern int CM_Get_Device_ID(IntPtr devNode, StringBuilder buffer, int bufferLen, int flags);

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
