using SmarTTY.GUI.Styles;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>Colours of the current WPF theme for the WinForms tool bars and their menus.</summary>
internal sealed class ToolbarPalette
{
    public Color Background { get; private set; }

    public Color Popup { get; private set; }

    public Color Input { get; private set; }

    public Color Text { get; private set; }

    public Color TextDisabled { get; private set; }

    public Color Hover { get; private set; }

    public Color Pressed { get; private set; }

    public Color Checked { get; private set; }

    public Color Accent { get; private set; }

    public Color Border { get; private set; }

    public Color Error { get; private set; }

    /// <summary>Reads the palette of the theme that is applied now (system colours before a theme is applied).</summary>
    public static ToolbarPalette FromCurrentTheme()
    {
        return new ToolbarPalette
        {
            Background = ThemeManager.GetColor("Theme.SurfaceBackground", SystemColors.Control),
            Popup = ThemeManager.GetColor("Theme.PopupBackground", SystemColors.Menu),
            Input = ThemeManager.GetColor("Theme.InputBackground", SystemColors.Window),
            Text = ThemeManager.GetColor("Theme.Text", SystemColors.ControlText),
            TextDisabled = ThemeManager.GetColor("Theme.TextDisabled", SystemColors.GrayText),
            Hover = ThemeManager.GetColor("Theme.ControlHoverBackground", SystemColors.ControlLight),
            Pressed = ThemeManager.GetColor("Theme.ControlPressedBackground", SystemColors.ControlDark),
            Checked = ThemeManager.GetColor("Theme.ItemSelectedBackground", SystemColors.Highlight),
            Accent = ThemeManager.GetColor("Theme.Accent", SystemColors.Highlight),
            Border = ThemeManager.GetColor("Theme.Border", SystemColors.ControlDark),
            Error = ThemeManager.GetColor("Theme.Error", Color.Firebrick)
        };
    }
}

/// <summary>
/// Draws a tool bar and its drop-down menus in the theme colours. The stock renderers paint the system (light)
/// background whatever BackColor is, which left light text on a light tool bar in the dark theme.
/// </summary>
internal sealed class ThemedToolStripRenderer : ToolStripProfessionalRenderer
{
    private readonly ToolbarPalette _palette;

    public ThemedToolStripRenderer(ToolbarPalette palette)
        : base(new ThemedColorTable(palette))
    {
        _palette = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // The tool bar itself has no frame; its drop-down menus keep theirs.
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(e);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Enabled items use their own ForeColor (or the link colour); the stock disabled grey is too dark on the
        // dark theme.
        if (!e.Item.Enabled)
        {
            e.TextColor = _palette.TextDisabled;
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item == null || e.Item.Enabled ? _palette.Text : _palette.TextDisabled;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // The stock check mark is a black bitmap, invisible on the dark menu background.
        Rectangle box = new Rectangle(e.ImageRectangle.Left - 2, e.ImageRectangle.Top - 2, e.ImageRectangle.Width + 3, e.ImageRectangle.Height + 3);
        using (SolidBrush background = new SolidBrush(_palette.Checked))
        {
            e.Graphics.FillRectangle(background, box);
        }
        using (Pen border = new Pen(_palette.Accent))
        {
            e.Graphics.DrawRectangle(border, box);
        }
        if (e.Item is ToolStripMenuItem { Image: not null })
        {
            e.Graphics.DrawImage(e.Item.Image, e.ImageRectangle);
            return;
        }
        Rectangle r = e.ImageRectangle;
        SmoothingMode smoothingMode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (Pen mark = new Pen(e.Item.Enabled ? _palette.Text : _palette.TextDisabled, 2f))
        {
            e.Graphics.DrawLines(mark, new PointF[]
            {
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.5f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f)
            });
        }
        e.Graphics.SmoothingMode = smoothingMode;
    }

    private sealed class ThemedColorTable : ProfessionalColorTable
    {
        private readonly ToolbarPalette _palette;

        public ThemedColorTable(ToolbarPalette palette)
        {
            _palette = palette;
            UseSystemColors = false;
        }

        public override Color ToolStripGradientBegin => _palette.Background;

        public override Color ToolStripGradientMiddle => _palette.Background;

        public override Color ToolStripGradientEnd => _palette.Background;

        public override Color ToolStripBorder => _palette.Background;

        public override Color MenuStripGradientBegin => _palette.Background;

        public override Color MenuStripGradientEnd => _palette.Background;

        public override Color OverflowButtonGradientBegin => _palette.Background;

        public override Color OverflowButtonGradientMiddle => _palette.Background;

        public override Color OverflowButtonGradientEnd => _palette.Background;

        public override Color GripDark => _palette.Border;

        public override Color GripLight => _palette.Background;

        public override Color SeparatorDark => _palette.Border;

        public override Color SeparatorLight => _palette.Background;

        public override Color ButtonSelectedHighlight => _palette.Hover;

        public override Color ButtonSelectedGradientBegin => _palette.Hover;

        public override Color ButtonSelectedGradientMiddle => _palette.Hover;

        public override Color ButtonSelectedGradientEnd => _palette.Hover;

        public override Color ButtonSelectedBorder => _palette.Border;

        public override Color ButtonSelectedHighlightBorder => _palette.Border;

        public override Color ButtonPressedHighlight => _palette.Pressed;

        public override Color ButtonPressedGradientBegin => _palette.Pressed;

        public override Color ButtonPressedGradientMiddle => _palette.Pressed;

        public override Color ButtonPressedGradientEnd => _palette.Pressed;

        public override Color ButtonPressedBorder => _palette.Border;

        public override Color ButtonPressedHighlightBorder => _palette.Border;

        public override Color ButtonCheckedHighlight => _palette.Checked;

        public override Color ButtonCheckedGradientBegin => _palette.Checked;

        public override Color ButtonCheckedGradientMiddle => _palette.Checked;

        public override Color ButtonCheckedGradientEnd => _palette.Checked;

        public override Color ButtonCheckedHighlightBorder => _palette.Accent;

        public override Color CheckBackground => _palette.Checked;

        public override Color CheckSelectedBackground => _palette.Checked;

        public override Color CheckPressedBackground => _palette.Checked;

        public override Color MenuItemSelected => _palette.Hover;

        public override Color MenuItemSelectedGradientBegin => _palette.Hover;

        public override Color MenuItemSelectedGradientEnd => _palette.Hover;

        public override Color MenuItemBorder => _palette.Border;

        public override Color MenuItemPressedGradientBegin => _palette.Pressed;

        public override Color MenuItemPressedGradientMiddle => _palette.Pressed;

        public override Color MenuItemPressedGradientEnd => _palette.Pressed;

        public override Color MenuBorder => _palette.Border;

        public override Color ToolStripDropDownBackground => _palette.Popup;

        public override Color ImageMarginGradientBegin => _palette.Popup;

        public override Color ImageMarginGradientMiddle => _palette.Popup;

        public override Color ImageMarginGradientEnd => _palette.Popup;
    }
}
