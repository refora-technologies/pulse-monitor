using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Pulse.Views;

/// <summary>
/// A small graph of a reading's last minute, for the Sensors window: a line with a faint fill
/// under it, scaled to its own lowest and highest.
/// </summary>
/// <remarks>
/// Drawn rather than built from shapes, like <see cref="CoreBars"/>: there can be a few hundred
/// of these on a page, each redrawn at every reading.
/// </remarks>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<float>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// Oldest first.
    public IReadOnlyList<float>? Values
    {
        get => (IReadOnlyList<float>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Frozen(Color.FromRgb(0xA7, 0x8B, 0xFA)), FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// The value at the bottom of the graph, or NaN for the lowest one shown.
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// The value at the top of the graph, or NaN for the highest one shown.
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty ShowGuidesProperty = DependencyProperty.Register(
        nameof(ShowGuides), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// Faint lines at a quarter, half and three quarters, for a large graph read against its scale.
    public bool ShowGuides
    {
        get => (bool)GetValue(ShowGuidesProperty);
        set => SetValue(ShowGuidesProperty, value);
    }

    private static readonly Pen Guide = FrozenPen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 1);

    protected override Size MeasureOverride(Size available)
        => new(double.IsInfinity(available.Width) ? 80 : available.Width, double.IsInfinity(available.Height) ? 20 : available.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        if (ShowGuides)
            for (int q = 1; q <= 3; q++)
            {
                double y = Math.Round(h * q / 4) + 0.5;
                dc.DrawLine(Guide, new Point(0, y), new Point(w, y));
            }

        if (values is null || values.Count < 2) return;

        // A fixed range when one is given, so a line's height means the same thing every time it
        // is seen; otherwise the minute's own lowest and highest.
        double lo = double.IsNaN(Minimum) ? values.Min() : Math.Min(Minimum, values.Min());
        double hi = double.IsNaN(Maximum) ? values.Max() : Math.Max(Maximum, values.Max());
        bool fixedRange = !double.IsNaN(Minimum) && !double.IsNaN(Maximum);
        double range = hi - lo;
        // Room for the line's width and the dot at the top and bottom of an auto-ranged graph.
        double pad = fixedRange ? 1.5 : Math.Min(4, h * 0.15);
        // Whatever has been gathered spans the width, so a window opened seconds ago already has
        // a graph rather than a sliver at the right; after a minute it is the last minute.
        double step = (w - 2.5) / (values.Count - 1);   // room for the dot at the end
        double x0 = 0;
        // A flat reading sits in the middle rather than on the floor.
        Point At(int i) => new(x0 + i * step, range <= 0 ? h / 2 : h - pad - (values[i] - lo) / range * (h - 2 * pad));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(0), false, false);
            for (int i = 1; i < values.Count; i++) g.LineTo(At(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < values.Count; i++) g.LineTo(At(i), true, false);
            g.LineTo(new Point(At(values.Count - 1).X, h), true, false);
        }
        area.Freeze();

        var stroke = Stroke;
        dc.DrawGeometry(Fill(stroke), null, area);
        dc.DrawGeometry(null, Pen(stroke), line);

        // The newest point, so "now" is found at a glance.
        dc.DrawEllipse(stroke, null, At(values.Count - 1), 2.2, 2.2);
    }

    private static Pen FrozenPen(Color c, double width)
    {
        var pen = new Pen(Frozen(c), width);
        pen.Freeze();
        return pen;
    }

    private Brush? _fillFor;
    private Brush? _fill;
    private Pen? _pen;

    private Brush Fill(Brush stroke)
    {
        if (_fillFor != stroke || _fill is null)
        {
            _fillFor = stroke;
            var c = stroke is SolidColorBrush s ? s.Color : Color.FromRgb(0xA7, 0x8B, 0xFA);
            var fill = new LinearGradientBrush(Color.FromArgb(0x2C, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
            fill.Freeze();
            _fill = fill;
            var pen = new Pen(stroke, 1.4) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            _pen = pen;
        }
        return _fill;
    }

    private Pen Pen(Brush stroke) => _pen ?? new Pen(stroke, 1.4);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// A grid of equal cards that takes as many columns as fit at a minimum width, so the Sensors
/// window's cards reflow as it is resized.
/// </summary>
public sealed class ResponsiveGrid : UniformGrid
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        // As many as fit, even with fewer cards, so a card is the same width on every page.
        int fit = double.IsInfinity(available.Width) ? 3 : Math.Max(1, (int)(available.Width / MinColumnWidth));
        if (Columns != fit) Columns = fit;
        return base.MeasureOverride(available);
    }
}
