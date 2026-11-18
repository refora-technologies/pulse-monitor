using System.Windows;
using System.Windows.Media;
using Pulse.ViewModels;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Pulse.Views;

/// <summary>
/// The CPU Cores tile's reading: one thin bar per core, each filled to how busy that core is and
/// coloured the way a value is, green, amber or red.
/// </summary>
/// <remarks>
/// Drawn rather than built from an element per bar. Twelve to sixty-four bars, redrawn at every
/// reading, as elements would be that many objects measured and arranged each time for what is
/// a handful of rectangles. The in-game overlay draws the same bars the same way (InGame/src/ui.cpp),
/// so the two look alike.
/// </remarks>
public sealed class CoreBars : FrameworkElement
{
    public static readonly DependencyProperty LoadsProperty = DependencyProperty.Register(
        nameof(Loads), typeof(IReadOnlyList<float>), typeof(CoreBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// Each core's load, 0 to 100.
    public IReadOnlyList<float>? Loads
    {
        get => (IReadOnlyList<float>?)GetValue(LoadsProperty);
        set => SetValue(LoadsProperty, value);
    }

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(double), typeof(CoreBars),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// The width of one bar; the gap between two is a third of it, at least one pixel.
    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    /// The thresholds CPU Usage colours by, so a core reads the same colour the whole processor would.
    public const float Warn = 70, Danger = 90;

    private static readonly Brush Track = TileViewModel.Frozen(Color.FromArgb(0x55, 0x2A, 0x2A, 0x3A));

    private double Gap => Math.Max(1, Math.Round(BarWidth / 3));

    protected override Size MeasureOverride(Size available)
    {
        int n = Loads?.Count ?? 0;
        double width = n == 0 ? 0 : n * BarWidth + (n - 1) * Gap;
        double height = double.IsInfinity(available.Height) ? 16 : available.Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var loads = Loads;
        if (loads is null || loads.Count == 0) return;

        double h = ActualHeight, w = BarWidth, gap = Gap;
        for (int i = 0; i < loads.Count; i++)
        {
            double x = i * (w + gap);
            float load = Math.Clamp(loads[i], 0f, 100f);
            double filled = Math.Max(1, Math.Round(h * load / 100.0));
            var brush = load >= Danger ? TileViewModel.DangerBrush
                      : load >= Warn   ? TileViewModel.WarnBrush
                      :                  TileViewModel.NormalBrush;

            dc.DrawRectangle(Track, null, new Rect(x, 0, w, h));
            dc.DrawRectangle(brush, null, new Rect(x, h - filled, w, filled));
        }
    }
}
