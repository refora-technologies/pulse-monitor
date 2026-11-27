using System.Windows;
using System.Windows.Media;
using Pulse.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using WpfSize = System.Windows.Size;

namespace Pulse.Views;

/// <summary>
/// The battery tile's power sign, beside its name (27 Nov 2026, without making the tile any larger): a bolt while
/// charging, a plug when the charger is in but not charging (full, or held at a charge limit),
/// and a small battery filled to its level when running on it. Nothing when Windows cannot say.
/// </summary>
/// Drawn, not taken from a font, so it is the same everywhere and can be drawn in games too.
/// Designed on an 11 unit tall box and scaled to Height.
public sealed class PowerGlyph : FrameworkElement
{
    public static readonly DependencyProperty PowerProperty = DependencyProperty.Register(
        nameof(Power), typeof(BatteryPower), typeof(PowerGlyph),
        new FrameworkPropertyMetadata(BatteryPower.Unknown,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(float?), typeof(PowerGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// The colour of the battery's fill: the reading's own, green, amber or red.
    public static readonly DependencyProperty LevelBrushProperty = DependencyProperty.Register(
        nameof(LevelBrush), typeof(WpfBrush), typeof(PowerGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BatteryPower Power { get => (BatteryPower)GetValue(PowerProperty); set => SetValue(PowerProperty, value); }
    public float? Level { get => (float?)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public WpfBrush? LevelBrush { get => (WpfBrush?)GetValue(LevelBrushProperty); set => SetValue(LevelBrushProperty, value); }

    // Charging is the good news, so it is green; the plug is Pulse's violet; the battery's
    // outline is as quiet as the units.
    internal static readonly WpfBrush BoltBrush    = Frozen(WpfColor.FromRgb(0x3D, 0xDC, 0x97));
    internal static readonly WpfBrush PlugBrush    = Frozen(WpfColor.FromRgb(0xA7, 0x8B, 0xFA));
    internal static readonly WpfBrush OutlineBrush = Frozen(WpfColor.FromRgb(0x8E, 0x8A, 0xB0));

    private static WpfBrush Frozen(WpfColor c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// Width of each sign on the 11 tall design box.
    internal static double DesignWidth(BatteryPower p) => p switch
    {
        BatteryPower.Charging  => 7,
        BatteryPower.PluggedIn => 8,
        BatteryPower.OnBattery => 14,
        _                      => 0,
    };

    private double Scale => (double.IsNaN(Height) || Height <= 0 ? 11 : Height) / 11.0;

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double w = DesignWidth(Power);
        return w <= 0 ? new WpfSize(0, 0) : new WpfSize(w * Scale, 11 * Scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Power == BatteryPower.Unknown) return;
        double s = Scale;
        dc.PushTransform(new ScaleTransform(s, s));
        switch (Power)
        {
            case BatteryPower.Charging:  dc.DrawGeometry(BoltBrush, null, Bolt); break;
            case BatteryPower.PluggedIn: dc.DrawGeometry(PlugBrush, null, Plug); break;
            case BatteryPower.OnBattery: DrawBattery(dc); break;
        }
        dc.Pop();
    }

    private void DrawBattery(DrawingContext dc)
    {
        // A 12 by 7 body with a small nub, centred in the 11 tall box; filled from the left to
        // the level, with a sliver showing even when nearly empty so it never reads as missing.
        var pen = new WpfPen(OutlineBrush, 1);
        dc.DrawRoundedRectangle(null, pen, new Rect(0.5, 2.5, 11.5, 6), 1.6, 1.6);
        dc.DrawRoundedRectangle(OutlineBrush, null, new Rect(12.6, 4.2, 1.4, 2.6), 0.5, 0.5);
        double level = Math.Clamp((Level ?? 0) / 100.0, 0, 1);
        double inner = 8.5, fill = Math.Max(1, inner * level);
        dc.DrawRoundedRectangle(LevelBrush ?? OutlineBrush, null, new Rect(2, 4, fill, 3), 0.6, 0.6);
    }

    private static readonly Geometry Bolt = Freeze(Geometry.Parse(
        "M 4.6,0 L 0.4,6.2 L 3.3,6.2 L 2.4,11 L 6.6,4.6 L 3.7,4.6 Z"));

    // Two prongs, a rounded body, and the cable leaving from below.
    private static readonly Geometry Plug = Freeze(Geometry.Parse(
        "M 1.9,0 h 1.2 v 3 h -1.2 Z " +
        "M 4.9,0 h 1.2 v 3 h -1.2 Z " +
        "M 0.5,3.4 h 7 v 1.8 c 0,1.9 -1.3,3.2 -3,3.2 h -1 c -1.7,0 -3,-1.3 -3,-3.2 Z " +
        "M 3.4,8.2 h 1.2 v 2.8 h -1.2 Z"));

    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
}
