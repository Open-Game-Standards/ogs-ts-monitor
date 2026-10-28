using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ogs.Telemetry.Monitor.Controls;

/// <summary>One trace of a <see cref="StripChart"/>. <see cref="Values"/> may
/// hold NaN where the quantity wasn't sent; those samples are gaps.
/// <see cref="MinSpan"/> stops a quiet signal being zoomed into its noise.</summary>
public sealed record StripLane(string Label, string Unit, double[] Times, double[] Values, double MinSpan, string Current);

/// <summary>
/// Stacked, individually auto-scaled time histories over the last
/// <see cref="Window"/> seconds (newest at the right), so a car's 2° of roll
/// reads as clearly as an aircraft's 60°. Each lane shows its label, current
/// value and the range it is scaled to.
/// </summary>
public sealed class StripChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<StripLane>?> LanesProperty =
        AvaloniaProperty.Register<StripChart, IReadOnlyList<StripLane>?>(nameof(Lanes));
    public static readonly StyledProperty<double> NowProperty =
        AvaloniaProperty.Register<StripChart, double>(nameof(Now));
    public static readonly StyledProperty<double> WindowProperty =
        AvaloniaProperty.Register<StripChart, double>(nameof(Window), 30);

    static readonly Color[] Palette =
    {
        Color.Parse("#e06c5a"), Color.Parse("#5aa0e0"), Color.Parse("#f5d76e"),
        Color.Parse("#86b65a"), Color.Parse("#c58af0"), Color.Parse("#6fd6c8"), Color.Parse("#f0a35a"),
    };
    static readonly IBrush LaneBack = new SolidColorBrush(Color.FromArgb(14, 140, 170, 150));
    static readonly IPen ZeroPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 160, 180, 165)), 1, dashStyle: DashStyle.Dash);
    static readonly IBrush Muted = new SolidColorBrush(Color.FromArgb(190, 160, 185, 165));
    static readonly IBrush Ink = new SolidColorBrush(Color.FromArgb(235, 225, 235, 220));
    static readonly Typeface Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    static StripChart()
    {
        AffectsRender<StripChart>(LanesProperty, NowProperty, WindowProperty);
    }

    public IReadOnlyList<StripLane>? Lanes { get => GetValue(LanesProperty); set => SetValue(LanesProperty, value); }
    /// <summary>The time at the right edge, on the lanes' time axis.</summary>
    public double Now { get => GetValue(NowProperty); set => SetValue(NowProperty, value); }
    public double Window { get => GetValue(WindowProperty); set => SetValue(WindowProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var lanes = Lanes;
        if (lanes is null || lanes.Count == 0 || Bounds.Width < 80) return;
        const double LabelWidth = 150, Gap = 3;
        double laneH = (Bounds.Height - Gap * (lanes.Count - 1)) / lanes.Count;
        if (laneH < 14) return;
        double plotX = LabelWidth, plotW = Bounds.Width - LabelWidth - 4;
        double t1 = Now, t0 = Now - Window;

        for (int li = 0; li < lanes.Count; li++)
        {
            var lane = lanes[li];
            double y = li * (laneH + Gap);
            var rect = new Rect(0, y, Bounds.Width, laneH);
            ctx.FillRectangle(LaneBack, rect, 4f);
            var color = Palette[li % Palette.Length];

            // label and current value share one line in the left column
            var label = Text(lane.Label, 11, new SolidColorBrush(color));
            var current = Text(lane.Current, 11, Ink);
            double ty = y + (laneH - label.Height) / 2;
            ctx.DrawText(label, new Point(6, ty));
            ctx.DrawText(current, new Point(LabelWidth - current.Width - 8, ty));

            // range: the data's extent, at least MinSpan, centred on the data
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int i = 0; i < lane.Values.Length; i++)
            {
                if (lane.Times[i] < t0) continue;
                double v = lane.Values[i];
                if (double.IsNaN(v)) continue;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (double.IsInfinity(lo))
            {
                var none = Text("not sent", 10, Muted);
                ctx.DrawText(none, new Point(plotX + 6, y + (laneH - none.Height) / 2));
                continue;
            }
            double mid = (lo + hi) / 2, span = Math.Max(lane.MinSpan, (hi - lo) * 1.2);
            lo = mid - span / 2;
            hi = mid + span / 2;
            double Y(double v) => y + laneH - 3 - (v - lo) / (hi - lo) * (laneH - 6);
            double X(double t) => plotX + (t - t0) / (t1 - t0) * plotW;

            if (lo < 0 && hi > 0) ctx.DrawLine(ZeroPen, new Point(plotX, Y(0)), new Point(plotX + plotW, Y(0)));

            var pen = new Pen(new SolidColorBrush(color), 1.6, lineJoin: PenLineJoin.Round);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < lane.Values.Length; i++)
                {
                    double t = lane.Times[i], v = lane.Values[i];
                    if (t < t0 || double.IsNaN(v)) { if (open) { g.EndFigure(false); open = false; } continue; }
                    var p = new Point(X(t), Y(v));
                    if (!open) { g.BeginFigure(p, false); open = true; }
                    else g.LineTo(p);
                }
                if (open) g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);

            // scale at the old (left) end, where it doesn't cover the newest data
            var range = Text($"{Format(hi)}…{Format(lo)} {lane.Unit}", 9, Muted);
            ctx.DrawText(range, new Point(plotX + 4, y + 1));
        }
    }

    static string Format(double v)
    {
        double a = Math.Abs(v);
        string f = a >= 100 ? "0" : a >= 10 ? "0.0" : a >= 1 ? "0.0" : "0.00";
        return v.ToString("+" + f + ";-" + f + ";0", CultureInfo.InvariantCulture);
    }

    static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);
}
