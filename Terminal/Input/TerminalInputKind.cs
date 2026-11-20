namespace SmarTTY.Terminal.Input;

/// <summary>Where bytes that a terminal view sends to the remote side come from (the values of <c>TerminalEmulatorControl.InputKind</c>).</summary>
public enum TerminalInputKind
{
    /// <summary>Typed characters.</summary>
    Typed,

    /// <summary>The escape sequence of a key, or a reply to a query of the program.</summary>
    KeySequence,

    /// <summary>A mouse report.</summary>
    MouseReport,

    /// <summary>Pasted text.</summary>
    Paste
}
