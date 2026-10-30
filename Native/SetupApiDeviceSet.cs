using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SmarTTY.Native;

public class SetupApiDeviceSet : IDisposable
{
    public struct ComPortInfo
    {
        /// <summary>The number of a port named COMn; 0 for a port with another name (a virtual port such as CNCA0).</summary>
        public int PortNumber;

        /// <summary>The name the port is opened by ("COM3", "CNCA0"), as the device itself records it.</summary>
        public string PortName;

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

    /// <summary>The device classes that have serial ports: Ports (COM and LPT) and Modem (3G dongles, GSM modems).</summary>
    private static readonly Guid[] PortClasses =
    {
        new Guid("4d36e978-e325-11ce-bfc1-08002be10318"),
        new Guid("4d36e96d-e325-11ce-bfc1-08002be10318")
    };

    private const int DicsFlagGlobal = 1;

    private const int DirefDev = 1;

    private const int DirefDrv = 2;

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, int scope, int hwProfile, int keyType, int samDesired);

    /// <summary>All ports of all classes that have them, each name once.</summary>
    public static ComPortInfo[] EnumerateAllComPorts()
    {
        List<ComPortInfo> ports = new List<ComPortInfo>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Guid portClass in PortClasses)
        {
            using (SetupApiDeviceSet deviceSet = new SetupApiDeviceSet(portClass))
            {
                foreach (ComPortInfo port in deviceSet.EnumerateComPorts())
                {
                    if (seen.Add(port.PortName))
                    {
                        ports.Add(port);
                    }
                }
            }
        }
        return ports.ToArray();
    }

    /// <summary>
    /// The port name a device records in its registry key ("PortName", or "AttachedTo" for a modem); only when that is
    /// missing is it read from the friendly name ("USB Serial Port (COM3)"), which is shown to people and may be translated.
    /// </summary>
    private string ReadPortName(ref SpDevInfoData data, string friendlyName)
    {
        foreach (int keyType in new[] { DirefDev, DirefDrv })
        {
            IntPtr key = SetupDiOpenDevRegKey(_deviceInfoSet, ref data, DicsFlagGlobal, 0, keyType, 1 /* KEY_QUERY_VALUE */);
            if (key == InvalidHandleValue || key == IntPtr.Zero)
            {
                continue;
            }
            try
            {
                using (Microsoft.Win32.RegistryKey registryKey = Microsoft.Win32.RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(key, ownsHandle: true)))
                {
                    foreach (string valueName in new[] { "PortName", "AttachedTo" })
                    {
                        if (registryKey.GetValue(valueName) is string name && name.Trim().Length > 0)
                        {
                            return name.Trim();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException || ex is ObjectDisposedException)
            {
            }
        }
        int comIndex = friendlyName?.LastIndexOf("(COM", StringComparison.Ordinal) ?? -1;
        if (comIndex != -1)
        {
            int closingParenIndex = friendlyName.IndexOf(')', comIndex);
            if (closingParenIndex != -1)
            {
                string name = friendlyName.Substring(comIndex + 1, closingParenIndex - comIndex - 1);
                return int.TryParse(name.Substring(3), out _) ? name : null;
            }
        }
        return null;
    }

    private static int PortNumberOf(string portName)
    {
        return portName != null && portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(portName.Substring(3), out int number) && number > 0 ? number : 0;
    }

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
        if (currentDeviceId != null && !currentDeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
        {
            currentDeviceId = GetParentDeviceId(currentDeviceId);
        }
        if (currentDeviceId != null && currentDeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
        {
            isUsbDevice = true;
        }
        else
        {
            isUsbDevice = false;
        }
        while (currentDeviceId != null && currentDeviceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            currentDeviceId = GetParentDeviceId(currentDeviceId);
        }
        string text = null;
        int slashIndex = currentDeviceId?.LastIndexOf('\\') ?? (-1);
        if (slashIndex > 0)
        {
            text = currentDeviceId.Substring(slashIndex + 1);
        }
        // A device without a serial number gets an ID from Windows ("6&2a1b3c&0&2") that depends on the USB port it is
        // plugged into: it identifies the socket, not the device, and is not shown as a serial number.
        if (text != null && text.IndexOf('&') >= 0)
        {
            text = null;
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
                    string portName = ReadPortName(ref spDevInfoData, friendlyName);
                    if (portName != null)
                    {
                        string description = GetRegistryProperty(_deviceInfoSet, ref spDevInfoData, DeviceRegistryProperty.DeviceDescription);
                        yield return new ComPortInfo
                        {
                            Description = description ?? friendlyName,
                            PortName = portName,
                            PortNumber = PortNumberOf(portName),
                            InstanceId = stringBuilder.ToString()
                        };
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
