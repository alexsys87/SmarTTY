using SmarTTY.Controls;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmarTTY.Forms;

/// <summary>
/// Per-monitor DPI support for the WinForms parts of the application.
/// </summary>
/// <remarks>
/// The process is per-monitor DPI aware (app.manifest), so Windows no longer stretches the WinForms windows: they
/// lay themselves out for the DPI of their monitor and again when they are moved to a monitor with another scale.
/// Designer sizes are for 96 DPI; fonts in points are rendered by GDI at the system DPI (the scale of the primary
/// monitor at sign-in), so on another monitor they are scaled by monitor DPI / system DPI.
/// WinForms itself only does this when opted in through app.config, which also changes how every form scales; the
/// application keeps its own scaling instead (DpiAwareForm, DpiAwareControl).
/// </remarks>
internal static class DpiScaling
{
    public const int DesignDpi = 96;

    public const int WM_DPICHANGED = 0x02E0;

    public const int WM_DPICHANGED_AFTERPARENT = 0x02E3;

    private static int _systemDpi;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>The DPI GDI uses for fonts in points.</summary>
    public static int SystemDpi
    {
        get
        {
            if (_systemDpi == 0)
            {
                try
                {
                    _systemDpi = (int)GetDpiForSystem();
                }
                catch (EntryPointNotFoundException)
                {
                    // Before Windows 10 1607 every window has the system DPI.
                    using Graphics graphics = Graphics.FromHwnd(IntPtr.Zero);
                    _systemDpi = (int)graphics.DpiX;
                }
                if (_systemDpi <= 0)
                {
                    _systemDpi = DesignDpi;
                }
            }
            return _systemDpi;
        }
    }

    /// <summary>The DPI of the monitor the window is on (the system DPI where Windows cannot tell).</summary>
    public static int GetWindowDpi(IntPtr hwnd)
    {
        try
        {
            int dpi = (int)GetDpiForWindow(hwnd);
            if (dpi > 0)
            {
                return dpi;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        return SystemDpi;
    }

    /// <summary>The new DPI and the suggested window rectangle carried by WM_DPICHANGED.</summary>
    public static void ReadDpiChanged(ref Message m, out int dpi, out Rectangle suggestedBounds)
    {
        dpi = (int)((long)m.WParam & 0xFFFF);
        RECT rect = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
        suggestedBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>
    /// Rescales the fonts of a control tree laid out for <paramref name="oldDpi"/> to <paramref name="newDpi"/>:
    /// the root font and every font set explicitly below it (inherited fonts follow their parent). Text browsers
    /// (terminals, editors) keep the font chosen in the settings and get the absolute scale instead.
    /// <paramref name="scaleRootFont"/> scales the root font even when it is inherited from outside the tree.
    /// </summary>
    public static void ScaleFonts(Control root, int oldDpi, int newDpi, bool scaleRootFont)
    {
        if (oldDpi == newDpi)
        {
            return;
        }
        float factor = (float)newDpi / oldDpi;
        float absoluteScale = (float)newDpi / SystemDpi;
        // Collected before anything changes: an inherited font is the very object of its parent.
        List<Control> explicitFonts = new List<Control>();
        List<ToolStripItem> explicitItemFonts = new List<ToolStripItem>();
        List<ReadonlyTextBrowser> textBrowsers = new List<ReadonlyTextBrowser>();
        List<ToolStrip> toolStrips = new List<ToolStrip>();
        CollectFonts(root, scaleRootFont, explicitFonts, explicitItemFonts, textBrowsers, toolStrips);
        foreach (Control control in explicitFonts)
        {
            control.Font = ScaleFont(control.Font, factor);
        }
        foreach (ToolStripItem item in explicitItemFonts)
        {
            item.Font = ScaleFont(item.Font, factor);
        }
        foreach (ToolStrip toolStrip in toolStrips)
        {
            Size imageSize = toolStrip.ImageScalingSize;
            toolStrip.ImageScalingSize = new Size((int)Math.Round(imageSize.Width * factor), (int)Math.Round(imageSize.Height * factor));
        }
        foreach (ReadonlyTextBrowser textBrowser in textBrowsers)
        {
            textBrowser.SetFontDpiScale(absoluteScale);
        }
    }

    private static void CollectFonts(Control control, bool scaleAsExplicit, List<Control> explicitFonts, List<ToolStripItem> explicitItemFonts, List<ReadonlyTextBrowser> textBrowsers, List<ToolStrip> toolStrips)
    {
        if (control is ReadonlyTextBrowser textBrowser)
        {
            textBrowsers.Add(textBrowser);
        }
        else if (scaleAsExplicit || control.Parent == null || !ReferenceEquals(control.Font, control.Parent.Font))
        {
            explicitFonts.Add(control);
        }
        if (control is ToolStrip toolStrip)
        {
            toolStrips.Add(toolStrip);
            foreach (ToolStripItem item in toolStrip.Items)
            {
                if (!(item is ToolStripControlHost) && !ReferenceEquals(item.Font, toolStrip.Font))
                {
                    explicitItemFonts.Add(item);
                }
            }
        }
        foreach (Control child in control.Controls)
        {
            CollectFonts(child, scaleAsExplicit: false, explicitFonts, explicitItemFonts, textBrowsers, toolStrips);
        }
    }

    public static Font ScaleFont(Font font, float factor)
    {
        return new Font(font.FontFamily, font.Size * factor, font.Style, font.Unit, font.GdiCharSet, font.GdiVerticalFont);
    }

    /// <summary>Scales the bounds of a control tree; a docked root is undocked meanwhile so that its own size scales too.</summary>
    public static void ScaleLayout(Control root, float factor)
    {
        DockStyle dock = root.Dock;
        if (dock != DockStyle.None)
        {
            root.Dock = DockStyle.None;
        }
        root.Scale(new SizeF(factor, factor));
        if (dock != DockStyle.None)
        {
            root.Dock = dock;
        }
    }
}
