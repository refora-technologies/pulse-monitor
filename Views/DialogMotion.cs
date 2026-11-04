using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pulse.Views;

/// <summary>
/// How Pulse's dialogs come and go: they fade in and settle from slightly smaller, and fade out
/// when closed, so a question arrives rather than appears.
/// </summary>
/// <remarks>
/// Off when Windows' own animations are off (Settings, Accessibility, Visual effects), for anyone
/// who has asked for things not to move.
/// </remarks>
internal static class DialogMotion
{
    public static void Attach(Window window)
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        window.Opacity = 0;

        window.Loaded += (_, _) =>
        {
            if (window.Content is FrameworkElement root)
            {
                var scale = new ScaleTransform(0.94, 0.94);
                root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                root.RenderTransform = scale;
                var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
            window.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        };

        // Closing is held back for the fade, then let through.
        bool fading = false;
        window.Closing += (_, e) =>
        {
            if (fading) return;
            e.Cancel = true;
            fading = true;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) => window.Close();
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        };
    }
}
