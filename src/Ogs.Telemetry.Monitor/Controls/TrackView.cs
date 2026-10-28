using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Vector3 = System.Numerics.Vector3;

namespace Ogs.Telemetry.Monitor.Controls;

/// <summary>
/// Top-down track of an object's recent positions on the world ground plane
/// (OGS-TS: +X right, +Z forward, seen from above), auto-fitted with a scale
/// grid, a fading trail and a heading arrow (0° = +Z up the screen, turning
/// right = clockwise).
/// </summary>
public sealed class TrackView : Control
{
    public static readonly StyledProperty<IReadOnlyList<Vector3>?> PointsProperty =
        AvaloniaProperty.Register<TrackView, IReadOnlyList<Vector3>?>(nameof(Points));
    public static readonly StyledProperty<double> HeadingProperty =
        AvaloniaProperty.Register<TrackView, double>(nameof(Heading));
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<TrackView, bool>(nameof(IsLive));

    const double MinSpan = 20; // metres: a parked car doesn't zoom to the noise

    static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(55, 140, 170, 150)), 1);
    static readonly Color TrailColor = Color.Parse("#86b65a");
    static readonly IBrush Marker = new SolidColorBrush(Color.Parse("#f5d76e"));
    static readonly IPen MarkerEdge = new Pen(new SolidColorBrush(Color.Parse("#10261a")), 1.2);
    static readonly IBrush Muted = new SolidColorBrush(Color.FromArgb(180, 190, 210, 185));
    static readonly Typeface Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    static TrackView()
    {
        AffectsRender<TrackView>(PointsProperty, HeadingProperty, IsLiveProperty);
    }

    public IReadOnlyList<Vector3>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double s = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(s)) s = 240;
        return new Size(s, s);
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size).Deflate(8);
        if (b.Width < 40 || b.Height < 40) return;
        var pts = Points;
        if (pts is null || pts.Count == 0)
        {
            var t = Text("no position yet", 12, Muted);
            ctx.DrawText(t, new Point(b.Center.X - t.Width / 2, b.Center.Y - t.Height / 2));
            return;
        }

        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        float minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
        double span = Math.Max(MinSpan, Math.Max(maxX - minX, maxZ - minZ) * 1.15);
        double midX = (minX + maxX) / 2.0, midZ = (minZ + maxZ) / 2.0;
        double k = Math.Min(b.Width, b.Height) / span;
        Point Map(double x, double z) => new(b.Center.X + (x - midX) * k, b.Center.Y - (z - midZ) * k);

        // grid at a round step, labelled with its size
        double step = NiceStep(span / 5);
        double gx0 = Math.Floor((midX - span) / step) * step, gz0 = Math.Floor((midZ - span) / step) * step;
        using (ctx.PushClip(b))
        {
            for (double x = gx0; x <= midX + span; x += step)
                ctx.DrawLine(GridPen, Map(x, midZ - span), Map(x, midZ + span));
            for (double z = gz0; z <= midZ + span; z += step)
                ctx.DrawLine(GridPen, Map(midX - span, z), Map(midX + span, z));

            // trail in ten chunks, older chunks fainter (one polyline each)
            const int Chunks = 10;
            int n = pts.Count;
            for (int ch = 0; ch < Chunks; ch++)
            {
                int from = ch * (n - 1) / Chunks, to = (ch + 1) * (n - 1) / Chunks;
                if (to <= from) continue;
                byte alpha = (byte)(40 + 215 * (ch + 1) / Chunks);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, TrailColor.R, TrailColor.G, TrailColor.B)), 2.2,
                    lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var line = new StreamGeometry();
                using (var g = line.Open())
                {
                    g.BeginFigure(Map(pts[from].X, pts[from].Z), false);
                    for (int i = from + 1; i <= to; i++) g.LineTo(Map(pts[i].X, pts[i].Z));
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, line);
            }
            var start = Map(pts[0].X, pts[0].Z);
            ctx.DrawEllipse(null, new Pen(Muted, 1.2), start, 3.5, 3.5);

            // heading arrow at the newest position
            var now = Map(pts[^1].X, pts[^1].Z);
            double h = Heading * Math.PI / 180;
            var fwd = new Vector(Math.Sin(h), -Math.Cos(h));
            var side = new Vector(-fwd.Y, fwd.X);
            var arrow = new StreamGeometry();
            using (var g = arrow.Open())
            {
                g.BeginFigure(now + fwd * 13, true);
                g.LineTo(now - fwd * 7 + side * 7);
                g.LineTo(now - fwd * 3);
                g.LineTo(now - fwd * 7 - side * 7);
                g.EndFigure(true);
            }
            ctx.DrawGeometry(IsLive ? Marker : Muted, MarkerEdge, arrow);
        }

        // the scale always shows; the axis hint only when both fit side by side
        var scale = Text($"grid {Format(step)} m", 10, Muted);
        ctx.DrawText(scale, new Point(b.X + 2, b.Bottom - scale.Height));
        var axes = Text("+Z ↑  +X →", 10, Muted);
        if (scale.Width + axes.Width + 16 <= b.Width)
            ctx.DrawText(axes, new Point(b.Right - axes.Width - 2, b.Bottom - axes.Height));
    }

    static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double f = raw / mag;
        return (f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10) * mag;
    }

    static string Format(double v) => v.ToString(v >= 1 ? "0" : "0.##", CultureInfo.InvariantCulture);

    static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);
}
