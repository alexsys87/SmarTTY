using SmarTTY.Controls;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class DpiAwareForm : Form
{
    private class BorderlessToolStripRenderer : ToolStripSystemRenderer
    {
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
        }
    }

    private static SizeF _scaleFactor;

    private static Dictionary<int, Stack<DpiAwareForm>> _dialogStacks = new Dictionary<int, Stack<DpiAwareForm>>();

    private static bool? _isHighDpi;

    private int _extraHeight;

    private bool _isScaled;

    private bool _isScaling;

    // DPI the layout of the form is scaled for (0 until the window exists).
    private int _dpi;

    public AutoScaleMode CurrentAutoScaleMode => AutoScaleMode;

    public static SizeF ScaleFactor
    {
        get
        {
            if (_scaleFactor.Width == 0f)
            {
                float factor = (float)DpiScaling.SystemDpi / DpiScaling.DesignDpi;
                _scaleFactor = new SizeF(factor, factor);
            }
            return _scaleFactor;
        }
    }

    public static bool IsHighDpi
    {
        get
        {
            if (!_isHighDpi.HasValue)
            {
                SizeF sizeF = ScaleFactor;
                _isHighDpi = sizeF.Width != 1f && sizeF.Width != 0f && sizeF.Height != 1f && sizeF.Height != 0f;
            }
            return _isHighDpi.Value;
        }
    }

    public bool IsScaled => _isScaled;

    public override Size MaximumSize
    {
        get
        {
            return base.MaximumSize;
        }
        set
        {
            if (_extraHeight > 0 && !_isScaling)
            {
                base.MaximumSize = new Size(value.Width, value.Height + _extraHeight);
            }
            else
            {
                base.MaximumSize = value;
            }
        }
    }

    private static Stack<DpiAwareForm> GetDialogStack()
    {
        lock (_dialogStacks)
        {
            return _dialogStacks.GetOrInsertNew(Thread.CurrentThread.ManagedThreadId);
        }
    }

    public DpiAwareForm()
    {
        InitializeComponent();
        // The application icon instead of the generic WinForms one; most dialogs choose their own (FormIcons.Apply).
        Icon applicationIcon = FormIcons.Application;
        if (applicationIcon != null)
        {
            Icon = applicationIcon;
        }
        if (AutoScaleMode != AutoScaleMode.None)
        {
            AutoScaleMode = AutoScaleMode.None;
        }
        if (IsHighDpi)
        {
            _extraHeight = Height - ClientRectangle.Height - 38;
            if (_extraHeight < 0)
            {
                _extraHeight = 0;
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_isScaled)
        {
            ApplyDpiScaling(DpiScaling.GetWindowDpi(Handle));
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == DpiScaling.WM_DPICHANGED)
        {
            DpiScaling.ReadDpiChanged(ref m, out int dpi, out Rectangle suggestedBounds);
            FollowMonitorDpi(dpi, suggestedBounds);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Lays the form out for the DPI of the monitor it was created on (designer sizes are for 96 DPI).</summary>
    private void ApplyDpiScaling(int dpi)
    {
        _dpi = dpi;
        if (dpi != DpiScaling.DesignDpi)
        {
            ScaleWindow(new SizeF((float)dpi / DpiScaling.DesignDpi, (float)dpi / DpiScaling.DesignDpi));
        }
        // Fonts in points already come out at the system DPI.
        DpiScaling.ScaleFonts(this, DpiScaling.SystemDpi, dpi, scaleRootFont: true);
        _isScaled = true;
        foreach (Control control in Controls)
        {
            ScaleDpiChildren(control);
        }
    }

    /// <summary>The form was moved to a monitor with another scale: Windows suggests the new window rectangle.</summary>
    private void FollowMonitorDpi(int dpi, Rectangle suggestedBounds)
    {
        if (_isScaled && dpi != _dpi && _dpi != 0)
        {
            int oldDpi = _dpi;
            _dpi = dpi;
            float factor = (float)dpi / oldDpi;
            try
            {
                _isScaling = true;
                SuspendLayout();
                Scale(new SizeF(factor, factor));
                DpiScaling.ScaleFonts(this, oldDpi, dpi, scaleRootFont: true);
            }
            finally
            {
                ResumeLayout(performLayout: true);
                _isScaling = false;
            }
        }
        if (WindowState == FormWindowState.Normal)
        {
            Bounds = suggestedBounds;
        }
    }

    private void ScaleWindow(SizeF factor)
    {
        try
        {
            _isScaling = true;
            bool wasMaximized = false;
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
                wasMaximized = true;
            }
            Size size = new Size((int)((float)Width * factor.Width), (int)((float)Height * factor.Height));
            if (MinimumSize.Width > 10 && MinimumSize.Height > 10)
            {
                Size = MinimumSize;
            }
            Scale(factor);
            Size = size;
            if (wasMaximized)
            {
                WindowState = FormWindowState.Maximized;
            }
            if (StartPosition == FormStartPosition.CenterScreen)
            {
                CenterToScreen();
            }
            else if (StartPosition == FormStartPosition.CenterParent)
            {
                CenterToParent();
            }
        }
        finally
        {
            _isScaling = false;
        }
    }

    internal static void ScaleDpiChildren(Control parentControl)
    {
        if (parentControl is DpiAwareControl)
        {
            (parentControl as DpiAwareControl).MarkScaled();
        }
        if (!parentControl.HasChildren)
        {
            return;
        }
        foreach (Control control in parentControl.Controls)
        {
            DpiAwareForm.ScaleDpiChildren(control);
        }
    }

    public static void ConfigureToolStrip(ToolStrip toolStrip, bool useBorderlessRenderer = true)
    {
        toolStrip.AutoSize = false;
        toolStrip.Height = 27;
        if (IsHighDpi)
        {
            toolStrip.ImageScalingSize = new Size((int)((float)toolStrip.ImageScalingSize.Width * ScaleFactor.Width), (int)((float)toolStrip.ImageScalingSize.Height * ScaleFactor.Height));
        }
        toolStrip.Paint += (object sender, PaintEventArgs e) =>
        {
            if (!(sender is ToolStrip paintedToolStrip))
            {
                return;
            }
            Pen pen = new Pen(Color.Gray);
            foreach (ToolStripItem item in paintedToolStrip.Items)
            {
                if (item is ToolStripComboBox { Visible: not false } toolStripComboBox)
                {
                    Rectangle rect = new Rectangle(toolStripComboBox.ComboBox.Location.X - 1, toolStripComboBox.ComboBox.Location.Y - 1, toolStripComboBox.ComboBox.Size.Width + 1, toolStripComboBox.ComboBox.Size.Height + 1);
                    e.Graphics.DrawRectangle(pen, rect);
                }
                if (item is ToolStripTextBox { Visible: not false } toolStripTextBox)
                {
                    Rectangle textBoxRect = new Rectangle(toolStripTextBox.TextBox.Location.X - 1, toolStripTextBox.TextBox.Location.Y - 1, toolStripTextBox.TextBox.Size.Width + 1, toolStripTextBox.TextBox.Size.Height + 1);
                    e.Graphics.DrawRectangle(pen, textBoxRect);
                }
            }
        };
        if (useBorderlessRenderer)
        {
            toolStrip.Renderer = new BorderlessToolStripRenderer();
        }
    }
}
