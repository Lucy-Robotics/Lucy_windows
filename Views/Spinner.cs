using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Lucy.Views;

/// <summary>LucyLoader: a circle in the secondary colour with a highlight top arc, 1s spin.</summary>
public sealed class Spinner : Grid
{
    public Spinner()
    {
        var ring = new Ellipse { StrokeThickness = 3 };
        ring.SetResourceReference(Shape.StrokeProperty, "LineStrongBrush");
        var arc = new Ellipse
        {
            StrokeThickness = 3,
            StrokeDashCap = PenLineCap.Flat,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        arc.SetResourceReference(Shape.StrokeProperty, "HighlightBrush");
        arc.SetResourceReference(EffectProperty, "GlowSoft");
        var rotate = new RotateTransform();
        arc.RenderTransform = rotate;
        Children.Add(ring);
        Children.Add(arc);

        SizeChanged += (_, _) =>
        {
            // Quarter-circle dash: circumference in stroke-thickness units.
            var d = Math.Max(1, Math.Min(ActualWidth, ActualHeight) - 3);
            var circumference = Math.PI * d / 3;
            arc.StrokeDashArray = new DoubleCollection { circumference / 4, circumference };
        };
        Loaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        Unloaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}
