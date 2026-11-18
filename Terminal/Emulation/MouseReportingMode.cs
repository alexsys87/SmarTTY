namespace SmarTTY.Terminal.Emulation;

/// <summary>What the terminal reports of the mouse to the program (CSI ? 9 h, 1000 h ... 1003 h); the number is the number of the mode.</summary>
public enum MouseReportingMode
{
    Off = 0,
    X10 = 9,
    Normal = 1000,
    Highlight = 1001,
    ButtonEvent = 1002,
    AnyEvent = 1003
}
