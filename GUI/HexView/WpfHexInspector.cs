using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SmarTTY.GUI.HexView;

/// <summary>
/// The data inspector beside the bytes of <see cref="WpfHexEditor"/> (stage 3B-3): what <c>Controls/HexDataInspector</c> is for the WinForms view,
/// drawn with WPF. What it shows and decides is <see cref="HexInspectorModel"/> (the values at the caret, the byte order, the offset, the selection,
/// the coloured block, the message, the keys, the clicks, the edit of a value); this element draws it in device pixels like its owner, keeps the
/// edit box, the focus and the hand cursor, and copies to the clipboard. Its keys and clicks do not reach the bytes behind it.
/// </summary>
internal sealed class WpfHexInspector : FrameworkElement
{
    private readonly WpfHexEditor _owner;

    private readonly HexInspectorModel _model;

    private readonly VisualCollection _children;

    private readonly TextBox _editBox = new TextBox { Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1) };

    // Where the edit box is, in device pixels.
    private Int32Rect _editBounds;

    public WpfHexInspector(WpfHexEditor owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _model = new HexInspectorModel(owner);
        _children = new VisualCollection(this) { _editBox };
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        _editBox.PreviewKeyDown += OnEditBoxKeyDown;
        _editBox.LostKeyboardFocus += (sender, e) => EndEdit();
    }

    /// <summary>The width that shows a label and the longest value, in device pixels.</summary>
    public int PreferredWidth => 2 * HexInspectorModel.InnerMargin + (HexInspectorModel.LabelChars + HexInspectorModel.ValueChars) * CharWidth + 1;

    private double Scale => _owner.DeviceScale;

    private int CharWidth => Math.Max(1, _owner.CharWidthInPixels);

    // The height of the text and three pixels, as the WinForms inspector.
    private int RowHeight => Math.Max(1, _owner.TextHeightInPixels) + 3;

    private int ValueX => HexInspectorModel.InnerMargin + HexInspectorModel.LabelChars * CharWidth;

    private int WidthInPixels => (int)Math.Round(ActualWidth * Scale);

    private int HeightInPixels => (int)Math.Round(ActualHeight * Scale);

    // ---- drawing (in device pixels: the drawing is scaled back by the DPI)

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index)
    {
        return _children[index];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _editBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = Scale;
        double height = Math.Max(_editBounds.Height / scale, _editBox.DesiredSize.Height);
        _editBox.Arrange(new Rect(_editBounds.X / scale, _editBounds.Y / scale, _editBounds.Width / scale, height));
        return finalSize;
    }

    private FormattedText Text(string text, RgbColor color)
    {
        return new FormattedText(text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _owner.TextTypeface, Math.Max(1, _owner.EmSizeInPixels),
            _owner.BrushOf(color), Scale);
    }

    // One line of text in a box, cut with an ellipsis, centred in the height of the box.
    private void DrawText(DrawingContext context, string text, int x, int y, int width, int height, RgbColor color)
    {
        FormattedText formatted = Text(text, color);
        formatted.MaxTextWidth = Math.Max(1, width);
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        double top = y + Math.Max(0, (height - _owner.TextHeightInPixels) / 2);
        context.DrawText(formatted, new Point(x, top));
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        double scale = Scale;
        context.PushTransform(new ScaleTransform(1 / scale, 1 / scale));
        try
        {
            Render(context);
        }
        finally
        {
            context.Pop();
        }
    }

    private void Render(DrawingContext context)
    {
        RgbColor backColor = _owner.BackgroundColor;
        RgbColor foreColor = _owner.ForegroundColor;
        RgbColor back = HexByteText.Blend(backColor, foreColor, 0.05f);
        RgbColor fore = foreColor;
        RgbColor dim = HexByteText.Blend(backColor, foreColor, 0.55f);
        bool dark = HexByteText.IsDark(backColor);
        RgbColor link = dark ? RgbColor.FromArgb(120, 180, 255) : RgbColor.FromArgb(0, 90, 200);
        RgbColor error = dark ? RgbColor.FromArgb(255, 120, 120) : RgbColor.FromArgb(190, 30, 30);
        RgbColor highlight = FromWpf(SystemColors.HighlightColor);
        RgbColor highlightText = FromWpf(SystemColors.HighlightTextColor);
        int width = WidthInPixels;
        int height = HeightInPixels;
        context.DrawRectangle(_owner.BrushOf(back), null, new Rect(0, 0, width, height));
        context.DrawRectangle(_owner.BrushOf(HexByteText.Blend(backColor, foreColor, 0.3f)), null, new Rect(0, 0, 1, height));
        int margin = HexInspectorModel.InnerMargin;
        int rowHeight = RowHeight;
        int valueX = ValueX;
        int valueWidth = width - valueX - margin;
        int labelWidth = valueX - margin;
        int y = HexInspectorModel.TitleTop;
        DrawText(context, HexInspectorModel.Title, margin, y, Math.Max(1, width - margin), rowHeight, dim);
        Int32Rect hide = HideLinkBounds;
        DrawText(context, HexInspectorModel.HideText, hide.X, y, hide.Width, rowHeight, link);
        y += rowHeight + HexInspectorModel.BlockGap;

        DrawText(context, "Byte order", margin, y, labelWidth, rowHeight, dim);
        DrawText(context, _model.ByteOrderText, valueX, y, valueWidth, rowHeight, link);
        y += rowHeight;

        IReadOnlyList<HexInspectorRow> rows = _model.ReadRows();
        int selectedRow = _model.SelectedRow;
        bool focused = IsKeyboardFocused;
        for (int index = 0; index < rows.Count; index++)
        {
            HexInspectorRow row = rows[index];
            Rect rowBounds = new Rect(1, y, Math.Max(0, width - 1), rowHeight);
            bool selected = index == selectedRow && focused;
            if (selected)
            {
                context.DrawRectangle(_owner.BrushOf(highlight), null, rowBounds);
            }
            else if (index == _model.HoverRow || index == selectedRow)
            {
                context.DrawRectangle(_owner.BrushOf(HexByteText.Blend(back, fore, 0.1f)), null, rowBounds);
            }
            DrawText(context, row.Label, margin, y, labelWidth, rowHeight, selected ? highlightText : dim);
            DrawText(context, row.Value, valueX, y, valueWidth, rowHeight, selected ? highlightText : (row.Known ? fore : dim));
            y += rowHeight;
        }

        y += HexInspectorModel.BlockGap;
        _model.ForgetMessageIfMoved();
        DrawText(context, "Offset", margin, y, labelWidth, rowHeight, dim);
        DrawText(context, _model.OffsetText, valueX, y, valueWidth, rowHeight, fore);
        y += rowHeight;
        string selectionText = _model.SelectionText;
        if (selectionText != null)
        {
            DrawText(context, "Selection", margin, y, labelWidth, rowHeight, dim);
            DrawText(context, selectionText, valueX, y, valueWidth, rowHeight, fore);
            y += rowHeight;
        }
        HexHighlight block = _model.Highlight;
        if (block != null)
        {
            context.DrawRectangle(_owner.BrushOf(RgbColor.FromArgb(block.Argb)), null, new Rect(margin, y + 3, Math.Max(0, rowHeight - 6), Math.Max(0, rowHeight - 6)));
            DrawText(context, "Marked", margin + rowHeight, y, Math.Max(1, labelWidth - rowHeight), rowHeight, dim);
            DrawText(context, HexInspectorModel.HighlightText(block), valueX, y, valueWidth, rowHeight, fore);
            y += rowHeight;
        }

        y += HexInspectorModel.BlockGap;
        FormattedText hint = Text(_model.Hint, _model.MessageIsError ? error : dim);
        hint.MaxTextWidth = Math.Max(1, width - 2 * margin);
        hint.MaxTextHeight = Math.Max(rowHeight, height - y);
        context.DrawText(hint, new Point(margin, y));
    }

    private static RgbColor FromWpf(Color color)
    {
        return RgbColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    // The link in the title row that hides the inspector.
    private Int32Rect HideLinkBounds
    {
        get
        {
            int textWidth = (int)Math.Ceiling(Text(HexInspectorModel.HideText, _owner.ForegroundColor).WidthIncludingTrailingWhitespace);
            return new Int32Rect(Math.Max(0, WidthInPixels - HexInspectorModel.InnerMargin - textWidth), 0, textWidth, RowHeight + HexInspectorModel.BlockGap);
        }
    }

    private static bool Contains(Int32Rect bounds, int x, int y)
    {
        return x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;
    }

    private Int32Rect ValueBounds(int row)
    {
        int rowHeight = RowHeight;
        int y = HexInspectorModel.ValueRowTop(row, rowHeight);
        return new Int32Rect(ValueX - 3, y, Math.Max(20, WidthInPixels - ValueX - HexInspectorModel.InnerMargin + 3), rowHeight);
    }

    // ---- mouse (points in device pixels)

    private void DevicePoint(MouseEventArgs e, out int x, out int y)
    {
        Point point = e.GetPosition(this);
        x = (int)Math.Floor(point.X * Scale);
        y = (int)Math.Floor(point.Y * Scale);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        e.Handled = true;
        DevicePoint(e, out int x, out int y);
        int row = HexInspectorModel.RowAt(y, RowHeight);
        Cursor = HexInspectorModel.IsLink(row, x >= ValueX, Contains(HideLinkBounds, x, y)) ? Cursors.Hand : null;
        if (_model.SetHover(row))
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_model.SetHover(-1))
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        e.Handled = true;
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        DevicePoint(e, out int x, out int y);
        int row = HexInspectorModel.RowAt(y, RowHeight);
        if (e.ClickCount == 2)
        {
            if (row >= 0)
            {
                BeginEdit(row);
            }
            return;
        }
        switch (_model.Press(row, Contains(HideLinkBounds, x, y)))
        {
            case HexInspectorCommand.HideInspector:
                _owner.ShowDataInspector = false;
                _owner.Focus();
                return;
            case HexInspectorCommand.FocusEditor:
                _owner.Focus();
                return;
        }
        Focus();
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        e.Handled = true;
    }

    // ---- keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !ReferenceEquals(e.OriginalSource, this))
        {
            return;
        }
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (_model.Key((VirtualKey)KeyInterop.VirtualKeyFromKey(key), control))
        {
            case HexInspectorCommand.BeginEdit:
                BeginEdit(_model.SelectedRow);
                break;
            case HexInspectorCommand.FocusEditor:
                _owner.Focus();
                break;
            case HexInspectorCommand.HideInspector:
                _owner.ShowDataInspector = false;
                _owner.Focus();
                break;
            case HexInspectorCommand.CopyValue:
                CopySelectedValue();
                break;
            case HexInspectorCommand.NotHandled:
                return;
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        // What is typed here is not typed into the bytes.
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this))
        {
            _model.OnFocused();
        }
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    private void CopySelectedValue()
    {
        if (!_model.TryGetSelectedValue(out string value))
        {
            return;
        }
        try
        {
            Clipboard.SetText(value);
            _model.Copied();
        }
        catch (Exception exception)
        {
            _model.ShowMessage(exception.Message, isError: true);
        }
        InvalidateVisual();
    }

    // ---- the edit of a value

    private void BeginEdit(int row)
    {
        if (!_model.BeginEdit(row, out string value))
        {
            InvalidateVisual();
            return;
        }
        _editBounds = ValueBounds(row);
        _editBox.FontFamily = _owner.TextTypeface.FontFamily;
        _editBox.FontSize = Math.Max(1, _owner.EmSizeInPixels / Scale);
        _editBox.Background = _owner.BrushOf(_owner.BackgroundColor);
        _editBox.Foreground = _owner.BrushOf(_owner.ForegroundColor);
        _editBox.Padding = new Thickness(0);
        _editBox.Text = value;
        _editBox.Visibility = Visibility.Visible;
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
        _editBox.Focus();
        _editBox.SelectAll();
        InvalidateVisual();
    }

    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _model.CancelEdit();
            EndEdit();
            Focus();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            switch (_model.Commit(_editBox.Text))
            {
                case HexInspectorCommit.Unchanged:
                case HexInspectorCommit.Written:
                    EndEdit();
                    Focus();
                    break;
                case HexInspectorCommit.Rejected:
                    InvalidateVisual();
                    _editBox.SelectAll();
                    break;
            }
        }
    }

    private void EndEdit()
    {
        _model.EndEdit();
        if (_editBox.Visibility == Visibility.Visible)
        {
            _editBox.Visibility = Visibility.Collapsed;
        }
        InvalidateVisual();
    }
}
