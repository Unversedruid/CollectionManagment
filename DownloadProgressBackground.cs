using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CollectionDowngrader;

public sealed class DownloadProgressBackground : Control
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<DownloadProgressBackground, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<DownloadProgressBackground, IBrush?>(nameof(Fill));

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProgressProperty || change.Property == FillProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Fill is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var width = Bounds.Width * Math.Clamp(Progress, 0, 100) / 100;
        if (width > 0)
        {
            context.DrawRectangle(Fill, null, new Rect(0, 0, width, Bounds.Height));
        }
    }
}
