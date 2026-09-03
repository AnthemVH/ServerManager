using System.Windows;
using System.Windows.Media;

namespace ServerLauncher.App.Views;

/// <summary>
/// Minimal line chart for a fixed-range series such as CPU percentage. Drawn directly
/// rather than pulled in as a charting dependency: it renders one polyline and a fill,
/// and is redrawn a few times a minute.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var max = Math.Max(1d, Maximum);
        var stepX = ActualWidth / (values.Count - 1);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var start = new Point(0, PointFor(values[0], max));
            context.BeginFigure(start, isFilled: false, isClosed: false);

            for (var i = 1; i < values.Count; i++)
            {
                context.LineTo(new Point(i * stepX, PointFor(values[i], max)), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();

        var pen = new Pen(Stroke, 1.5);
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private double PointFor(double value, double max)
    {
        var clamped = Math.Clamp(value, 0d, max);
        return ActualHeight - (clamped / max * ActualHeight);
    }
}
