namespace SmarTTY.Utilities;

/// <summary>
/// The small rules of the progress window of a paste that takes time (<c>GUI/PasteProgressWindow</c>): the value of the
/// bar and where the window sits over the terminal. They carry no UI dependency, so the test harness can check them
/// without a window.
/// </summary>
public static class PasteProgressRules
{
    /// <summary>The bar value (0..100) for <paramref name="done"/> of <paramref name="total"/>; 0 when the total is unknown.</summary>
    public static int BarValue(long done, long total)
    {
        if (total <= 0)
        {
            return 0;
        }
        long value = done * 100 / total;
        if (value < 0)
        {
            return 0;
        }
        return value > 100 ? 100 : (int)value;
    }

    /// <summary>
    /// The left edge of the window on the screen: centered over the anchor, and never left of the anchor.
    /// </summary>
    public static double NearLeft(double anchorLeft, double anchorWidth, double windowWidth)
    {
        double offset = (anchorWidth - windowWidth) / 2;
        return anchorLeft + (offset > 0 ? offset : 0);
    }

    /// <summary>
    /// The top edge of the window on the screen: over the bottom edge of the anchor, lifted by <paramref name="margin"/>,
    /// and never above the top of the anchor.
    /// </summary>
    public static double NearTop(double anchorTop, double anchorBottom, double windowHeight, double margin)
    {
        double preferred = anchorBottom - windowHeight - margin;
        return preferred > anchorTop ? preferred : anchorTop;
    }
}
