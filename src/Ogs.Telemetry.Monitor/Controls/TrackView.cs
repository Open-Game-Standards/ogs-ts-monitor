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

    static readonly IPen StartPen = new Pen(Muted, 1.2);

    // the trail fades over ten chunks, oldest faintest: ten pens, made once
    static readonly IPen[] TrailPens = Enumerable.Range(0, 10).Select(ch => (IPen)new Pen(
        new SolidColorBrush(Color.FromArgb((byte)(40 + 215 * (ch + 1) / 10), TrailColor.R, TrailColor.G, TrailColor.B)), 2.2,
        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)).ToArray();

    static Geometry? _arrow;

    static Geometry Arrow()
    {
        if (_arrow is not null) return _arrow;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(0, 13), true);
            g.LineTo(new Point(7, -7));
            g.LineTo(new Point(0, -3));
            g.LineTo(new Point(-7, -7));
            g.EndFigure(true);
        }
        return _arrow = geo;
    }

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

    // The trail is 30 s of history, rebuilt as ten polylines (native paths the garbage collector has to
    // free later). It moves slowly, so it is rebuilt five times a second and kept between frames; only
    // the heading arrow, which turns with the object, is drawn fresh each frame.
    static readonly long RebuildEvery = System.Diagnostics.Stopwatch.Frequency / 5;

    sealed class Scene
    {
        public required IReadOnlyList<Vector3> Pts;
        public required Rect Bounds;
        public required long BuiltAt;
        public required double Step;
        public required double GridX0, GridZ0, MidX, MidZ, Span;
        public required Func<double, double, Point> Map;
        public required Geometry?[] Trail;
        public required Point Start, Now;
    }

    Scene? _scene;
    bool _rebuildPending;

    Scene Build(IReadOnlyList<Vector3> pts, Rect b)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            var q = pts[i];
            if (q.X < minX) minX = q.X;
            if (q.X > maxX) maxX = q.X;
            if (q.Z < minZ) minZ = q.Z;
            if (q.Z > maxZ) maxZ = q.Z;
        }
        double span = Math.Max(MinSpan, Math.Max(maxX - minX, maxZ - minZ) * 1.15);
        double midX = (minX + maxX) / 2.0, midZ = (minZ + maxZ) / 2.0;
        double k = Math.Min(b.Width, b.Height) / span;
        Point Map(double x, double z) => new(b.Center.X + (x - midX) * k, b.Center.Y - (z - midZ) * k);

        // trail in ten chunks, older chunks fainter (one polyline each)
        const int Chunks = 10;
        int n = pts.Count;
        var trail = new Geometry?[Chunks];
        for (int ch = 0; ch < Chunks; ch++)
        {
            int from = ch * (n - 1) / Chunks, to = (ch + 1) * (n - 1) / Chunks;
            if (to <= from) continue;
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                // 30 s of history is thousands of points, far more than there are pixels: a point
                // within 1.5 px of the last one kept adds nothing to the line, so skip it
                var last = Map(pts[from].X, pts[from].Z);
                g.BeginFigure(last, false);
                for (int i = from + 1; i <= to; i++)
                {
                    var p = Map(pts[i].X, pts[i].Z);
                    var d = p - last;
                    if (i != to && d.X * d.X + d.Y * d.Y < 2.25) continue;
                    g.LineTo(p);
                    last = p;
                }
                g.EndFigure(false);
            }
            trail[ch] = line;
        }

        double step = NiceStep(span / 5);
        return new Scene
        {
            Pts = pts, Bounds = b, BuiltAt = System.Diagnostics.Stopwatch.GetTimestamp(),
            Step = step,
            GridX0 = Math.Floor((midX - span) / step) * step, GridZ0 = Math.Floor((midZ - span) / step) * step,
            MidX = midX, MidZ = midZ, Span = span, Map = Map, Trail = trail,
            Start = Map(pts[0].X, pts[0].Z), Now = Map(pts[^1].X, pts[^1].Z),
        };
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size).Deflate(8);
        if (b.Width < 40 || b.Height < 40) return;
        var pts = Points;
        if (pts is null || pts.Count == 0)
        {
            _scene = null;
            var t = Text("no position yet", 12, Muted);
            ctx.DrawText(t, new Point(b.Center.X - t.Width / 2, b.Center.Y - t.Height / 2));
            return;
        }

        var sc = _scene;
        if (sc is null || sc.Bounds != b)
            sc = _scene = Build(pts, b);
        else if (!ReferenceEquals(sc.Pts, pts))
        {
            long wait = RebuildEvery - (System.Diagnostics.Stopwatch.GetTimestamp() - sc.BuiltAt);
            if (wait <= 0) sc = _scene = Build(pts, b);
            else if (!_rebuildPending)
            {
                // nothing else may redraw us once the track stops changing: come back for the newest
                _rebuildPending = true;
                Avalonia.Threading.DispatcherTimer.RunOnce(() => { _rebuildPending = false; InvalidateVisual(); },
                    TimeSpan.FromSeconds((double)wait / System.Diagnostics.Stopwatch.Frequency + 0.01));
            }
        }

        var map = sc.Map;
        double midX = sc.MidX, midZ = sc.MidZ, span = sc.Span, step = sc.Step;
        using (ctx.PushClip(b))
        {
            // grid at a round step, labelled with its size
            for (double x = sc.GridX0; x <= midX + span; x += step)
                ctx.DrawLine(GridPen, map(x, midZ - span), map(x, midZ + span));
            for (double z = sc.GridZ0; z <= midZ + span; z += step)
                ctx.DrawLine(GridPen, map(midX - span, z), map(midX + span, z));

            for (int ch = 0; ch < sc.Trail.Length; ch++)
                if (sc.Trail[ch] is { } line) ctx.DrawGeometry(null, TrailPens[ch], line);
            ctx.DrawEllipse(null, StartPen, sc.Start, 3.5, 3.5);

            // heading arrow at the newest position
            var now = sc.Now;
            double h = Heading * Math.PI / 180;
            var fwd = new Vector(Math.Sin(h), -Math.Cos(h));
            var side = new Vector(-fwd.Y, fwd.X);
            // the arrow is one fixed shape (x across, y forward) turned and moved to the newest position
            var toScreen = new Matrix(side.X, side.Y, fwd.X, fwd.Y, now.X, now.Y);
            using (ctx.PushTransform(toScreen))
                ctx.DrawGeometry(IsLive ? Marker : Muted, MarkerEdge, Arrow());
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
        Ogs.Telemetry.Monitor.TextCache.Get(s, size, Mono, brush);
}
