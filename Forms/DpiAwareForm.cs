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

    public AutoScaleMode CurrentAutoScaleMode => AutoScaleMode;

    public static SizeF ScaleFactor
    {
        get
        {
            if (_scaleFactor.Width == 0f)
            {
                using Form form = new Form();
                using Graphics graphics = form.CreateGraphics();
                _scaleFactor = new SizeF(graphics.DpiX / 96f, graphics.DpiY / 96f);
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

    public DialogResult ShowModal()
    {
        Stack<DpiAwareForm> stack = GetDialogStack();
        try
        {
            stack.Push(this);
            return ShowDialog();
        }
        finally
        {
            try
            {
                stack.Pop();
            }
            catch
            {
            }
        }
    }

    public static DpiAwareForm GetActiveDialog()
    {
        Stack<DpiAwareForm> stack = GetDialogStack();
        if (stack != null && stack.Count != 0)
        {
            return stack.Peek();
        }
        return null;
    }

    public DpiAwareForm()
    {
        InitializeComponent();
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
        if (!_isScaled && IsHighDpi)
        {
            ApplyDpiScaling();
        }
    }

    private void ApplyDpiScaling()
    {
        try
        {
            _isScaling = true;
            bool flag = false;
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
                flag = true;
            }
            Size size = new Size((int)((float)Width * ScaleFactor.Width), (int)((float)Height * ScaleFactor.Height));
            if (MinimumSize.Width > 10 && MinimumSize.Height > 10)
            {
                Size = MinimumSize;
            }
            Scale(ScaleFactor);
            Size = size;
            if (flag)
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
            _isScaled = true;
            foreach (Control control in Controls)
            {
                ScaleDpiChildren(control);
            }
        }
        finally
        {
            _isScaling = false;
        }
    }

    internal static void ScaleDpiChildren(Control control2)
    {
        if (control2 is DpiAwareControl)
        {
            (control2 as DpiAwareControl).MarkScaled();
        }
        if (!control2.HasChildren)
        {
            return;
        }
        foreach (Control control in control2.Controls)
        {
            DpiAwareForm.ScaleDpiChildren(control);
        }
    }

    public static void ConfigureToolStrip(ToolStrip toolStrip2, bool flag = true)
    {
        toolStrip2.AutoSize = false;
        toolStrip2.Height = 27;
        if (IsHighDpi)
        {
            toolStrip2.ImageScalingSize = new Size((int)((float)toolStrip2.ImageScalingSize.Width * ScaleFactor.Width), (int)((float)toolStrip2.ImageScalingSize.Height * ScaleFactor.Height));
        }
        toolStrip2.Paint += (object obj, PaintEventArgs e) =>
        {
            if (!(obj is ToolStrip toolStrip))
            {
                return;
            }
            Pen pen = new Pen(Color.Gray);
            foreach (ToolStripItem item in toolStrip.Items)
            {
                if (item is ToolStripComboBox { Visible: not false } toolStripComboBox)
                {
                    Rectangle rect = new Rectangle(toolStripComboBox.ComboBox.Location.X - 1, toolStripComboBox.ComboBox.Location.Y - 1, toolStripComboBox.ComboBox.Size.Width + 1, toolStripComboBox.ComboBox.Size.Height + 1);
                    e.Graphics.DrawRectangle(pen, rect);
                }
                if (item is ToolStripTextBox { Visible: not false } toolStripTextBox)
                {
                    Rectangle rect2 = new Rectangle(toolStripTextBox.TextBox.Location.X - 1, toolStripTextBox.TextBox.Location.Y - 1, toolStripTextBox.TextBox.Size.Width + 1, toolStripTextBox.TextBox.Size.Height + 1);
                    e.Graphics.DrawRectangle(pen, rect2);
                }
            }
        };
        if (flag)
        {
            toolStrip2.Renderer = new BorderlessToolStripRenderer();
        }
    }
}
