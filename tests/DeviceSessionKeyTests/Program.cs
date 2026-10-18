using System;
using VisualTTY.DeviceTerminal;

// Regression harness for "one COM port - one session". Run with: dotnet run --project tests/DeviceSessionKeyTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static int Main()
    {
        Check(DeviceSessionKey.ForSerialPort("COM3") == DeviceSessionKey.ForSerialPort("com3"), "port names are case-insensitive");
        Check(DeviceSessionKey.ForSerialPort(" COM3 ") == DeviceSessionKey.ForSerialPort("COM3"), "surrounding spaces are ignored");
        Check(DeviceSessionKey.ForSerialPort(@"\\.\COM12") == DeviceSessionKey.ForSerialPort("COM12"), "device path form is the same port");
        Check(DeviceSessionKey.ForSerialPort("COM3") != DeviceSessionKey.ForSerialPort("COM4"), "different ports have different keys");
        Check(DeviceSessionKey.ForSerialPort("COM1") != DeviceSessionKey.ForSerialPort("COM11"), "COM1 and COM11 are not confused");
        Check(DeviceSessionKey.ForSerialPort(null) == null, "null name has no key");
        Check(DeviceSessionKey.ForSerialPort("  ") == null, "blank name has no key");
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
