using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace CollectionDowngrader;

public sealed class StarRangeSlider : Control
{
    private enum Thumb
    {
        Lower,
        Upper
    }

    private const double ThumbRadius = 8;
    private const double HorizontalPadding = 10;
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#FF66AB"));
    private Thumb activeThumb;

    public event EventHandler? RangeChanged;

    public double Minimum { get; set; } = 0;
    public double Maximum { get; set; } = 20;
    public double LowerValue { get; private set; }
    public double UpperValue { get; private set; } = 20;
    public bool IsFiltering { get; private set; }

    public StarRangeSlider()
    {
        Focusable = true;
        Height = 32;
    }

    public void ClearRange()
    {
        LowerValue = Minimum;
        UpperValue = Maximum;
        IsFiltering = false;
        RangeChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var left = HorizontalPadding;
        var right = Math.Max(left, Bounds.Width - HorizontalPadding);
        var y = Bounds.Height / 2;
        var lowerX = ToPosition(LowerValue, left, right);
        var upperX = ToPosition(UpperValue, left, right);

        context.DrawLine(new Pen(Brushes.Gray, 4), new Point(left, y), new Point(right, y));
        context.DrawLine(new Pen(AccentBrush, 4), new Point(lowerX, y), new Point(upperX, y));
        context.DrawEllipse(Brushes.White, new Pen(AccentBrush, 2), new Point(lowerX, y), ThumbRadius, ThumbRadius);
        context.DrawEllipse(Brushes.White, new Pen(AccentBrush, 2), new Point(upperX, y), ThumbRadius, ThumbRadius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var x = e.GetPosition(this).X;
        var lowerX = ToPosition(LowerValue, HorizontalPadding, Bounds.Width - HorizontalPadding);
        var upperX = ToPosition(UpperValue, HorizontalPadding, Bounds.Width - HorizontalPadding);
        activeThumb = Math.Abs(x - lowerX) <= Math.Abs(x - upperX) ? Thumb.Lower : Thumb.Upper;
        e.Pointer.Capture(this);
        UpdateFromPosition(x);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this)
        {
            UpdateFromPosition(e.GetPosition(this).X);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var delta = e.Key switch
        {
            Key.Left or Key.Down => -0.1,
            Key.Right or Key.Up => 0.1,
            _ => 0
        };
        if (delta == 0)
        {
            return;
        }

        UpdateValue(activeThumb == Thumb.Lower ? LowerValue + delta : UpperValue + delta);
        e.Handled = true;
    }

    private double ToPosition(double value, double left, double right) =>
        left + (right - left) * (value - Minimum) / (Maximum - Minimum);

    private void UpdateFromPosition(double x)
    {
        var left = HorizontalPadding;
        var right = Math.Max(left + 1, Bounds.Width - HorizontalPadding);
        var ratio = Math.Clamp((x - left) / (right - left), 0, 1);
        UpdateValue(Minimum + ratio * (Maximum - Minimum));
    }

    private void UpdateValue(double value)
    {
        value = Math.Round(Math.Clamp(value, Minimum, Maximum) * 10) / 10;
        if (activeThumb == Thumb.Lower)
        {
            LowerValue = Math.Min(value, UpperValue);
        }
        else
        {
            UpperValue = Math.Max(value, LowerValue);
        }

        IsFiltering = true;
        RangeChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
}
