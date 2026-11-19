using SmarTTY.Terminal.Input;
using SmarTTY.Terminal.Rendering;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SmarTTY.GUI.TerminalView;

/// <summary>A <see cref="TerminalRenderView"/> with its vertical scroll bar (the scrollback).</summary>
internal sealed class TerminalPanel : Grid
{
    private readonly ScrollBar _scrollBar = new ScrollBar { Orientation = Orientation.Vertical };

    private bool _updating;

    public TerminalPanel(XtermKeyTable keys)
    {
        View = new TerminalRenderView(keys);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(View, 0);
        SetColumn(_scrollBar, 1);
        Children.Add(View);
        Children.Add(_scrollBar);
        _scrollBar.Minimum = 0;
        _scrollBar.SmallChange = 1;
        _scrollBar.ValueChanged += OnScrollValueChanged;
        View.FrameRendered += UpdateScrollBar;
    }

    public TerminalRenderView View { get; }

    private void UpdateScrollBar()
    {
        TerminalDisplayModel model = View.Model;
        if (model == null)
        {
            return;
        }
        _updating = true;
        try
        {
            _scrollBar.Maximum = model.MaxTopLine;
            _scrollBar.ViewportSize = model.Rows;
            _scrollBar.LargeChange = Math.Max(1, model.Rows - 1);
            _scrollBar.Value = model.TopLine;
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnScrollValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating)
        {
            View.ScrollTo((int)Math.Round(e.NewValue));
        }
    }
}
