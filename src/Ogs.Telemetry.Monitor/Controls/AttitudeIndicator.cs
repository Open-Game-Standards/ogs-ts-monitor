using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ogs.Telemetry.Monitor.Controls;

/// <summary>
/// An attitude indicator in OGS-TS sign conventions: pitch nose-up positive
/// moves the horizon down, roll right-side-down positive tilts the horizon
/// counter-clockwise, as seen from the cockpit. Yaw is shown as a heading
/// readout (0° = +Z, turning right positive).
/// </summary>
public sealed class AttitudeIndicator : Control
{
    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<AttitudeIndicator, double>(nameof(Pitch));
    public static readonly StyledProperty<double> RollProperty =
        AvaloniaProperty.Register<AttitudeIndicator, double>(nameof(Roll));
    public static readonly StyledProperty<double> YawProperty =
        AvaloniaProperty.Register<AttitudeIndicator, double>(nameof(Yaw));
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<AttitudeIndicator, bool>(nameof(IsLive));

    /// <summary>Degrees of pitch from the centre to the rim.</summary>
    const double PitchToRim = 40;

    static readonly IBrush Sky = new SolidColorBrush(Color.Parse("#2f6fa3"));
    static readonly IBrush Ground = new SolidColorBrush(Color.Parse("#6b4f2a"));
    static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#3a4a40"));
    static readonly IPen Horizon = new Pen(Brushes.White, 1.5);
    static readonly IPen Ladder = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.2);
    static readonly IPen Ring = new Pen(new SolidColorBrush(Color.Parse("#86b65a")), 3);
    static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#c9dcc0")), 1.5);
    static readonly IPen Symbol = new Pen(new SolidColorBrush(Color.Parse("#f5d76e")), 3.5,
        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    static readonly IBrush Pointer = new SolidColorBrush(Color.Parse("#f5d76e"));
    static readonly Typeface Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    static AttitudeIndicator()
    {
        AffectsRender<AttitudeIndicator>(PitchProperty, RollProperty, YawProperty, IsLiveProperty);
    }

    public double Pitch { get => GetValue(PitchProperty); set => SetValue(PitchProperty, value); }
    public double Roll { get => GetValue(RollProperty); set => SetValue(RollProperty, value); }
    public double Yaw { get => GetValue(YawProperty); set => SetValue(YawProperty, value); }
    /// <summary>False greys the dial: no pose has arrived (or it has stalled).</summary>
    public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

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
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double r = size / 2 - 18; // leave room for the roll scale
        double px = r / PitchToRim;
        double rad = Math.PI / 180;

        using (ctx.PushGeometryClip(new EllipseGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r))))
        {
            if (!IsLive)
            {
                ctx.FillRectangle(Idle, new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
            }
            else
            {
                var rotate = Matrix.CreateTranslation(-c.X, -c.Y)
                             * Matrix.CreateRotation(-Roll * rad)
                             * Matrix.CreateTranslation(c.X, c.Y);
                using (ctx.PushTransform(rotate))
                {
                    double hy = c.Y + Pitch * px;
                    ctx.FillRectangle(Sky, new Rect(c.X - 3 * r, hy - 6 * r, 6 * r, 6 * r));
                    ctx.FillRectangle(Ground, new Rect(c.X - 3 * r, hy, 6 * r, 6 * r));
                    ctx.DrawLine(Horizon, new Point(c.X - 3 * r, hy), new Point(c.X + 3 * r, hy));
                    for (int deg = -60; deg <= 60; deg += 5)
                    {
                        if (deg == 0) continue;
                        double y = hy - deg * px;
                        bool major = deg % 10 == 0;
                        double w = major ? r * 0.32 : r * 0.14;
                        ctx.DrawLine(Ladder, new Point(c.X - w, y), new Point(c.X + w, y));
                        if (major && r > 70) // ladder numbers only where they're legible
                        {
                            var label = Text(Math.Abs(deg).ToString(CultureInfo.InvariantCulture), 10, Brushes.White);
                            ctx.DrawText(label, new Point(c.X + w + 4, y - label.Height / 2));
                            ctx.DrawText(label, new Point(c.X - w - 4 - label.Width, y - label.Height / 2));
                        }
                    }
                }
            }
        }

        ctx.DrawEllipse(null, Ring, c, r, r);

        // roll scale above the dial, and the pointer that follows the horizon
        foreach (int a in new[] { -60, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60 })
        {
            double t = (a - 90) * rad;
            double inner = r + 3, outer = r + (a % 30 == 0 ? 13 : 8);
            ctx.DrawLine(Tick,
                new Point(c.X + inner * Math.Cos(t), c.Y + inner * Math.Sin(t)),
                new Point(c.X + outer * Math.Cos(t), c.Y + outer * Math.Sin(t)));
        }
        if (IsLive)
        {
            double t = (-Roll - 90) * rad;
            double tip = r + 2, back = r + 12, half = 6 * rad * 1.2;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(c.X + tip * Math.Cos(t), c.Y + tip * Math.Sin(t)), true);
                g.LineTo(new Point(c.X + back * Math.Cos(t - half), c.Y + back * Math.Sin(t - half)));
                g.LineTo(new Point(c.X + back * Math.Cos(t + half), c.Y + back * Math.Sin(t + half)));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Pointer, null, geo);
        }

        // fixed vehicle reference symbol
        double s = r * 0.42;
        var wings = new StreamGeometry();
        using (var g = wings.Open())
        {
            g.BeginFigure(new Point(c.X - s, c.Y), false);
            g.LineTo(new Point(c.X - s * 0.35, c.Y));
            g.LineTo(new Point(c.X - s * 0.18, c.Y + s * 0.18));
            g.LineTo(new Point(c.X, c.Y));
            g.LineTo(new Point(c.X + s * 0.18, c.Y + s * 0.18));
            g.LineTo(new Point(c.X + s * 0.35, c.Y));
            g.LineTo(new Point(c.X + s, c.Y));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, Symbol, wings);

        if (IsLive)
        {
            var heading = Text($"HDG {Normalise(Yaw):000}°", 11, Brushes.White);
            var box = new Rect(c.X - heading.Width / 2 - 6, c.Y + r * 0.62 - 2, heading.Width + 12, heading.Height + 4);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), box, 4);
            ctx.DrawText(heading, new Point(box.X + 6, box.Y + 2));
        }
        else
        {
            var waiting = Text("no pose", 12, new SolidColorBrush(Color.Parse("#c9dcc0")));
            ctx.DrawText(waiting, new Point(c.X - waiting.Width / 2, c.Y + r * 0.35));
        }
    }

    static double Normalise(double yaw) => ((Math.Round(yaw) % 360) + 360) % 360;

    static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);
}
