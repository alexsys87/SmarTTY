using SmarTTY.Utilities;

namespace VisualTTY;

/// <summary>
/// What a terminal does for the bell (BEL). It was the nested <c>TerminalEmulatorControl.BeepMode</c> of the WinForms terminal; the settings
/// file holds the names of the values, which stay as they were.
/// </summary>
public enum BeepMode
{
    None,
    [EnumDisplayText("Default system beep")]
    SystemBeep,
    [EnumDisplayText("Beep through PC speaker")]
    ConsoleBeep,
    [EnumDisplayText("Custom")]
    WaveFile
}
