namespace SmarTTY.Utilities;

/// <summary>The edges of the window the mouse is near, for the minimalistic mode.</summary>
public static class ScreenEdgeUtils
{
    public enum EdgeFlags
    {
        None = 0,
        Top = 1,
        Left = 2,
        Right = 4,
        Bottom = 8
    }
}
