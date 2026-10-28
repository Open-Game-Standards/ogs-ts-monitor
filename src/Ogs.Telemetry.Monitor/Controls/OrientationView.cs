using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Ogs.Telemetry.Monitor.Controls;

/// <summary>
/// The object's orientation drawn as a box with its body axes (x right,
/// y up, z forward) over a gravity-aligned ground grid. Works at any
/// attitude: inverted, vertical, tumbling. In chase mode the heading is
/// taken out, so the view always looks from behind and slightly above and
/// right, and pitch and roll read the same whichever way the object faces;
/// world mode shows the full orientation against fixed world axes.
/// </summary>
public sealed class OrientationView : Control
{
    public static readonly StyledProperty<Quaternion> OrientationProperty =
        AvaloniaProperty.Register<OrientationView, Quaternion>(nameof(Orientation), Quaternion.Identity);
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<OrientationView, bool>(nameof(IsLive));
    public static readonly StyledProperty<bool> ChaseProperty =
        AvaloniaProperty.Register<OrientationView, bool>(nameof(Chase), true);

    static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 140, 170, 150)), 1);
    static readonly IBrush Shadow = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
    static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromArgb(230, 230, 240, 225)), 1.4);
    static readonly Color Body = Color.Parse("#4c7a2a");
    static readonly IBrush NoseFill = new SolidColorBrush(Color.FromArgb(220, 245, 215, 110));
    static readonly IPen AxisX = new Pen(new SolidColorBrush(Color.Parse("#e06c5a")), 2.5, lineCap: PenLineCap.Round);
    static readonly IPen AxisY = new Pen(new SolidColorBrush(Color.Parse("#86b65a")), 2.5, lineCap: PenLineCap.Round);
    static readonly IPen AxisZ = new Pen(new SolidColorBrush(Color.Parse("#5aa0e0")), 2.5, lineCap: PenLineCap.Round);
    static readonly Typeface Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    // Camera: look along +Z from behind, swung well round to the right and
    // tilted down, so all three axes keep a readable length.
    static readonly Quaternion Camera = Quaternion.Normalize(
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, -24f * MathF.PI / 180f)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -55f * MathF.PI / 180f));

    static readonly Vector3 Half = new(0.55f, 0.24f, 1.0f); // box half-extents: right, up, forward
    const float GroundY = -0.75f;

    float _heading; // last good heading for chase mode, radians

    static OrientationView()
    {
        AffectsRender<OrientationView>(OrientationProperty, IsLiveProperty, ChaseProperty);
        // the ground grid extends past the box; keep it inside the control
        ClipToBoundsProperty.OverrideDefaultValue<OrientationView>(true);
    }

    public Quaternion Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public bool Chase { get => GetValue(ChaseProperty); set => SetValue(ChaseProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double s = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(s)) s = 240;
        return new Size(s, s);
    }

    public override void Render(DrawingContext ctx)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 40) return;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2 + size * 0.04);
        double scale = size / 3.6;

        var q = Orientation;
        if (q.LengthSquared() < 1e-6f) q = Quaternion.Identity;
        q = Quaternion.Normalize(q);
        if (Chase)
        {
            var f = Vector3.Transform(Vector3.UnitZ, q);
            // heading is undefined pointing straight up/down: hold the last one
            if (MathF.Abs(f.Y) < 0.95f) _heading = MathF.Atan2(f.X, f.Z);
            q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -_heading) * q);
        }

        (Point P, float Depth) Project(Vector3 world)
        {
            var v = Vector3.Transform(world, Camera);
            return (new Point(c.X + v.X * scale, c.Y - v.Y * scale), v.Z);
        }

        // ground grid (gravity-aligned; never rotates with the object)
        for (int i = -4; i <= 4; i++)
        {
            float t = i * 0.5f;
            ctx.DrawLine(GridPen, Project(new Vector3(t, GroundY, -2f)).P, Project(new Vector3(t, GroundY, 2f)).P);
            ctx.DrawLine(GridPen, Project(new Vector3(-2f, GroundY, t)).P, Project(new Vector3(2f, GroundY, t)).P);
        }

        // the box's 8 corners in the (de-headed) world frame
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3(
                (i & 1) == 0 ? -Half.X : Half.X,
                (i & 2) == 0 ? -Half.Y : Half.Y,
                (i & 4) == 0 ? -Half.Z : Half.Z);
            corners[i] = Vector3.Transform(local, q);
        }

        // shadow: corners dropped straight down onto the ground, hulled
        var shadow = Hull(corners.Select(p => Project(new Vector3(p.X, GroundY, p.Z)).P).ToList());
        if (shadow.Count >= 3) ctx.DrawGeometry(Shadow, null, Polygon(shadow));

        if (!IsLive)
        {
            var waiting = Text("no pose", 12, new SolidColorBrush(Color.Parse("#c9dcc0")));
            ctx.DrawText(waiting, new Point(c.X - waiting.Width / 2, c.Y - waiting.Height / 2));
            return;
        }

        // faces, far to near; the front (+z) face is the nose
        int[][] faces =
        {
            new[] { 0, 1, 3, 2 }, // back  (-z)
            new[] { 4, 6, 7, 5 }, // front (+z)
            new[] { 0, 2, 6, 4 }, // left  (-x)
            new[] { 1, 5, 7, 3 }, // right (+x)
            new[] { 0, 4, 5, 1 }, // bottom(-y)
            new[] { 2, 3, 7, 6 }, // top   (+y)
        };
        var projected = corners.Select(Project).ToArray();
        foreach (var (face, index) in faces.Select((f, i) => (f, i))
                     .OrderByDescending(x => x.f.Average(k => projected[k].Depth)))
        {
            var pts = face.Select(k => projected[k].P).ToList();
            IBrush fill = index switch
            {
                1 => NoseFill,
                5 => new SolidColorBrush(Lighten(Body, 0.35), 0.9),
                4 => new SolidColorBrush(Lighten(Body, -0.35), 0.9),
                _ => new SolidColorBrush(Body, 0.82),
            };
            ctx.DrawGeometry(fill, Edge, Polygon(pts));
        }

        // body axes from the centre
        var o = Project(Vector3.Zero).P;
        // small sizes: short axis labels, and the caption only if it fits
        bool compact = size < 200;
        DrawAxis(ctx, o, Project(Vector3.Transform(new Vector3(1.15f, 0, 0), q)).P, AxisX, compact ? "x" : "x right");
        DrawAxis(ctx, o, Project(Vector3.Transform(new Vector3(0, 0.95f, 0), q)).P, AxisY, compact ? "y" : "y up");
        DrawAxis(ctx, o, Project(Vector3.Transform(new Vector3(0, 0, 1.65f), q)).P, AxisZ, compact ? "z" : "z fwd");

        var brush = new SolidColorBrush(Color.FromArgb(170, 200, 215, 195));
        var mode = Text(Chase ? "chase view · heading removed" : "world view", 10, brush);
        if (mode.Width > Bounds.Width - 12) mode = Text(Chase ? "chase" : "world", 10, brush);
        if (mode.Width <= Bounds.Width - 12)
            ctx.DrawText(mode, new Point(6, Bounds.Height - mode.Height - 4));
    }

    static void DrawAxis(DrawingContext ctx, Point from, Point to, IPen pen, string label)
    {
        ctx.DrawLine(pen, from, to);
        var d = to - from;
        double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (len > 1)
        {
            var u = new Vector(d.X / len, d.Y / len);
            var n = new Vector(-u.Y, u.X);
            ctx.DrawLine(pen, to, to - u * 8 + n * 4);
            ctx.DrawLine(pen, to, to - u * 8 - n * 4);
        }
        var t = Text(label, 10, pen.Brush!);
        ctx.DrawText(t, new Point(to.X + 4, to.Y - t.Height / 2));
    }

    static StreamGeometry Polygon(IList<Point> pts)
    {
        var g = new StreamGeometry();
        using var s = g.Open();
        s.BeginFigure(pts[0], true);
        for (int i = 1; i < pts.Count; i++) s.LineTo(pts[i]);
        s.EndFigure(true);
        return g;
    }

    /// <summary>Convex hull (monotone chain) of screen points.</summary>
    static List<Point> Hull(List<Point> pts)
    {
        pts = pts.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Point>();
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            int start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    static Color Lighten(Color c, double amount)
    {
        byte Mix(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);
}
