using SmarTTY.Forms;
using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class DpiAwareControl : UserControl
{
    private readonly bool _scaleOnCreate;

    private bool _isScaled;

    // DPI the layout of this control is scaled for (0 until it is scaled). Only the outermost DPI-aware control of a
    // tree hosted in WPF follows later DPI changes; everything inside it is rescaled together with it.
    private int _dpi;

    protected bool IsScaled => _isScaled;

    public AutoScaleMode CurrentAutoScaleMode => AutoScaleMode;

    public DpiAwareControl()
        : this(scaleOnCreate: false)
    {
    }

    public DpiAwareControl(bool scaleOnCreate)
    {
        InitializeComponent();
        _scaleOnCreate = scaleOnCreate;
        if (AutoScaleMode != AutoScaleMode.None)
        {
            AutoScaleMode = AutoScaleMode.None;
        }
    }

    internal virtual void MarkScaled()
    {
        _isScaled = true;
        foreach (object control in Controls)
        {
            if (control is DpiAwareControl)
            {
                (control as DpiAwareControl).MarkScaled();
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryScale();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Sent to the child windows after their top-level window (a WPF window) moved to a monitor with another DPI.
        if (m.Msg == DpiScaling.WM_DPICHANGED_AFTERPARENT)
        {
            FollowWindowDpi();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // A WPF host can also move the control into a window on another monitor without a DPI message.
        FollowWindowDpi();
    }

    private void TryScale()
    {
        if (_isScaled || !IsHandleCreated)
        {
            return;
        }
        DpiAwareForm dpiAwareForm = null;
        bool inWinFormsAdapter = false;
        for (Control control = Parent; control != null; control = control.Parent)
        {
            if (control?.GetType()?.Name == "WinFormsAdapter")
            {
                inWinFormsAdapter = true;
            }
            if (control is DpiAwareForm)
            {
                dpiAwareForm = control as DpiAwareForm;
                break;
            }
        }
        if (((dpiAwareForm != null && dpiAwareForm.IsScaled) || _scaleOnCreate) | inWinFormsAdapter)
        {
            ApplyScaling();
        }
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent != null)
        {
            TryScale();
        }
    }

    /// <summary>The DPI-aware form or control above this one, which scales this control together with itself.</summary>
    private Control FindScalingAncestor()
    {
        for (Control control = Parent; control != null; control = control.Parent)
        {
            if (control is DpiAwareForm || control is DpiAwareControl)
            {
                return control;
            }
        }
        return null;
    }

    private void ApplyScaling()
    {
        int dpi = DpiScaling.GetWindowDpi(Handle);
        // The outermost control of a WPF-hosted tree takes its font from the host, unscaled: it scales it itself.
        // A control added later into an already scaled tree inherits a scaled font and keeps it.
        bool outermost = FindScalingAncestor() == null;
        _dpi = dpi;
        if (dpi != DpiScaling.DesignDpi)
        {
            DpiScaling.ScaleLayout(this, (float)dpi / DpiScaling.DesignDpi);
        }
        DpiScaling.ScaleFonts(this, DpiScaling.SystemDpi, dpi, outermost);
        foreach (Control control in Controls)
        {
            DpiAwareForm.ScaleDpiChildren(control);
        }
        _isScaled = true;
    }

    private void FollowWindowDpi()
    {
        if (_dpi == 0 || !IsHandleCreated || FindScalingAncestor() != null)
        {
            return;
        }
        int dpi = DpiScaling.GetWindowDpi(Handle);
        if (dpi == _dpi)
        {
            return;
        }
        int oldDpi = _dpi;
        // Set first: scaling changes the size, which calls back here.
        _dpi = dpi;
        DpiScaling.ScaleFonts(this, oldDpi, dpi, scaleRootFont: true);
        DpiScaling.ScaleLayout(this, (float)dpi / oldDpi);
    }
}
