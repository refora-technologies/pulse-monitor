using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Pulse.Views;

/// Which sides of the overlay lie against the screen's edge.
[Flags]
public enum FlushEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>
/// The overlay's panel: Pulse's rounded rectangle, except on a side against the screen's edge,
/// which is flat, square at its corners, and flares into the edge with a small inward curve at
/// each end where it meets a rounded side, like a laptop's camera notch coming out of the display.
/// No outline is drawn along the screen's edge itself.
/// </summary>
/// <remarks>
/// The window overlay's version of the in-game overlay's panel (InGame/src/ui.cpp, Panel), which
/// came on 4 Nov 2026; the window overlay got it on 26 Nov 2026. The two follow the
/// same rules so a panel looks the same in a game and on the desktop. The flares reach past the
/// panel along the edge, so the panel reserves room for them outside its own rectangle.
/// </remarks>
public sealed class EdgePanel : Decorator
{
    public static readonly DependencyProperty FlushProperty = DependencyProperty.Register(
        nameof(Flush), typeof(FlushEdges), typeof(EdgePanel),
        new FrameworkPropertyMetadata(FlushEdges.None, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public FlushEdges Flush
    {
        get => (FlushEdges)GetValue(FlushProperty);
        set => SetValue(FlushProperty, value);
    }

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(EdgePanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(EdgePanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(EdgePanel),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty FlareRadiusProperty = DependencyProperty.Register(
        nameof(FlareRadius), typeof(double), typeof(EdgePanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// The inward curve where a rounded side meets the edge; 8 to Pulse's 12, as in games.
    public double FlareRadius
    {
        get => (double)GetValue(FlareRadiusProperty);
        set => SetValue(FlareRadiusProperty, value);
    }

    private bool Has(FlushEdges e) => (Flush & e) != 0;

    /// The room the flares take outside the panel, on each side.
    private Thickness Room()
    {
        double f = FlareRadius;
        bool horizontalEdge = Has(FlushEdges.Top) || Has(FlushEdges.Bottom);
        bool verticalEdge   = Has(FlushEdges.Left) || Has(FlushEdges.Right);
        return new Thickness(
            horizontalEdge && !Has(FlushEdges.Left)   ? f : 0,
            verticalEdge   && !Has(FlushEdges.Top)    ? f : 0,
            horizontalEdge && !Has(FlushEdges.Right)  ? f : 0,
            verticalEdge   && !Has(FlushEdges.Bottom) ? f : 0);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var room = Room();
        double extraW = room.Left + room.Right, extraH = room.Top + room.Bottom;
        if (Child is null) return new Size(extraW, extraH);
        Child.Measure(new Size(Math.Max(0, constraint.Width - extraW), Math.Max(0, constraint.Height - extraH)));
        return new Size(Child.DesiredSize.Width + extraW, Child.DesiredSize.Height + extraH);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var room = Room();
        Child?.Arrange(new Rect(room.Left, room.Top,
                                Math.Max(0, arrangeSize.Width - room.Left - room.Right),
                                Math.Max(0, arrangeSize.Height - room.Top - room.Bottom)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var room = Room();
        double left = room.Left, top = room.Top;
        double right = ActualWidth - room.Right, bottom = ActualHeight - room.Bottom;
        if (right <= left || bottom <= top) return;

        var outline = Outline(new Point(left, top), new Point(right, bottom));
        dc.DrawGeometry(Background, null, outline);
        if (BorderBrush is { } stroke)
        {
            var pen = new Pen(stroke, 1);
            pen.Freeze();
            dc.DrawGeometry(null, pen, outline);
        }
    }

    /// <summary>
    /// The panel's outline, clockwise from the top left: filled whole, stroked everywhere except
    /// along the screen's edge (those segments are not stroked).
    /// </summary>
    private PathGeometry Outline(Point p, Point q)
    {
        double r = Math.Min(CornerRadius, Math.Min(q.X - p.X, q.Y - p.Y) / 2), f = FlareRadius;
        bool L = Has(FlushEdges.Left), T = Has(FlushEdges.Top), R = Has(FlushEdges.Right), B = Has(FlushEdges.Bottom);

        // Each corner: where it is, the way the side coming into it runs, the way the side leaving
        // it runs, and whether each of those sides is against the edge.
        var corners = new (Point K, Vector In, Vector Out, bool InFlush, bool OutFlush)[]
        {
            (p,                   new Vector(0, -1), new Vector(1, 0),  L, T),
            (new Point(q.X, p.Y), new Vector(1, 0),  new Vector(0, 1),  T, R),
            (q,                   new Vector(0, 1),  new Vector(-1, 0), R, B),
            (new Point(p.X, q.Y), new Vector(-1, 0), new Vector(0, -1), B, L),
        };
        static Point At(Point k, Vector a, double x, Vector b, double y) => k + a * x + b * y;

        var figure = new PathFigure { IsClosed = false, IsFilled = true };
        bool firstStroked = true;
        Point? start = null;
        for (int i = 0; i < corners.Length; i++)
        {
            var (k, a, b, inFlush, outFlush) = corners[i];
            Point from, to;
            double radius;
            SweepDirection sweep;
            bool reachedStroked;   // whether the straight side arriving at this corner is drawn
            if (!inFlush && !outFlush)      { from = At(k, a, -r, b, 0); to = At(k, a, 0, b, r);  radius = r; sweep = SweepDirection.Clockwise; reachedStroked = true; }
            else if (inFlush && !outFlush)  { from = At(k, a, f, b, 0);  to = At(k, a, 0, b, f);  radius = f; sweep = SweepDirection.Counterclockwise; reachedStroked = false; }
            else if (!inFlush && outFlush)  { from = At(k, a, -f, b, 0); to = At(k, a, 0, b, -f); radius = f; sweep = SweepDirection.Counterclockwise; reachedStroked = true; }
            else                            { from = to = k; radius = 0; sweep = SweepDirection.Clockwise; reachedStroked = false; }

            if (start is null) { start = from; figure.StartPoint = from; firstStroked = reachedStroked; }
            else figure.Segments.Add(new LineSegment(from, reachedStroked));
            if (radius > 0) figure.Segments.Add(new ArcSegment(to, new Size(radius, radius), 0, false, sweep, true));
        }
        // Back to the start along the last side, drawn unless it lies against the edge.
        figure.Segments.Add(new LineSegment(start!.Value, firstStroked));

        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }
}
