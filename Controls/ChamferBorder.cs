using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lucy.Controls;

/// <summary>
/// The control panel's signature frame: top-right and bottom-left corners cut at 45°, the other
/// two square, 1px frame running along the diagonals. Optional HUD brackets on the square corners.
/// (lucy_control_panel@mra/fix-theme, index.css .chamfer / --hud-*)
/// </summary>
public class ChamferBorder : Decorator
{
    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ShowHudProperty = DependencyProperty.Register(
        nameof(ShowHud), typeof(bool), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HudBrushProperty = DependencyProperty.Register(
        nameof(HudBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public bool ShowHud { get => (bool)GetValue(ShowHudProperty); set => SetValue(ShowHudProperty, value); }
    public Brush? HudBrush { get => (Brush?)GetValue(HudBrushProperty); set => SetValue(HudBrushProperty, value); }
    /// <summary>Optional 3px accent bar down the left edge (joint category cards).</summary>
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    private Thickness Inner => new(
        Padding.Left + BorderThickness, Padding.Top + BorderThickness,
        Padding.Right + BorderThickness, Padding.Bottom + BorderThickness);

    protected override Size MeasureOverride(Size constraint)
    {
        var inner = Inner;
        var w = inner.Left + inner.Right;
        var h = inner.Top + inner.Bottom;
        if (Child == null) return new Size(w, h);
        Child.Measure(new Size(Math.Max(0, constraint.Width - w), Math.Max(0, constraint.Height - h)));
        return new Size(Child.DesiredSize.Width + w, Child.DesiredSize.Height + h);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var inner = Inner;
        Child?.Arrange(new Rect(inner.Left, inner.Top,
            Math.Max(0, arrangeSize.Width - inner.Left - inner.Right),
            Math.Max(0, arrangeSize.Height - inner.Top - inner.Bottom)));
        return arrangeSize;
    }

    public static Geometry Shape(double w, double h, double c, double inset = 0)
    {
        c = Math.Max(0, Math.Min(c, Math.Min(w, h) / 2));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(inset, inset), true, true);
            ctx.LineTo(new Point(w - c - inset * 0.4142, inset), true, false);
            ctx.LineTo(new Point(w - inset, c + inset * 0.4142), true, false);
            ctx.LineTo(new Point(w - inset, h - inset), true, false);
            ctx.LineTo(new Point(c + inset * 0.4142, h - inset), true, false);
            ctx.LineTo(new Point(inset, h - c - inset * 0.4142), true, false);
        }
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var t = BorderThickness;
        var fill = Shape(w, h, Chamfer);
        if (Background != null) dc.DrawGeometry(Background, null, fill);
        if (AccentBrush != null)
        {
            dc.PushClip(fill);
            dc.DrawRectangle(AccentBrush, null, new Rect(0, 0, 3, h));
            dc.Pop();
        }
        if (BorderBrush != null && t > 0)
        {
            var pen = new Pen(BorderBrush, t) { LineJoin = PenLineJoin.Miter };
            dc.DrawGeometry(null, pen, Shape(w, h, Chamfer, t / 2));
        }
        if (ShowHud && HudBrush != null)
        {
            const double len = 10, thick = 2;
            // top-left
            dc.DrawRectangle(HudBrush, null, new Rect(0, 0, len, thick));
            dc.DrawRectangle(HudBrush, null, new Rect(0, 0, thick, len));
            // bottom-right
            dc.DrawRectangle(HudBrush, null, new Rect(w - len, h - thick, len, thick));
            dc.DrawRectangle(HudBrush, null, new Rect(w - thick, h - len, thick, len));
        }
    }
}
