using SmarTTY.Forms;
using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

public partial class DpiAwareControl : UserControl
{
    private readonly bool _scaleOnCreate;

    private bool _isScaled;

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

    private void TryScale()
    {
        if (!DpiAwareForm.IsHighDpi || _isScaled)
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

    private void ApplyScaling()
    {
        DockStyle dock = Dock;
        if (dock != DockStyle.None)
        {
            Dock = DockStyle.None;
        }
        Scale(DpiAwareForm.ScaleFactor);
        if (dock != DockStyle.None)
        {
            Dock = dock;
        }
        foreach (Control control in Controls)
        {
            DpiAwareForm.ScaleDpiChildren(control);
        }
        _isScaled = true;
    }
}
