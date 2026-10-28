using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Ogs.Telemetry.Monitor;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One object in the sender list.</summary>
public sealed class ObjectItem(string key, string name, bool primary) : Observable
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public bool IsPrimary { get; } = primary;

    string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    bool _live;
    public bool IsLive { get => _live; set => Set(ref _live, value); }
}

/// <summary>One declared channel of the selected object; updated in place.</summary>
public sealed class ChannelRow : Observable
{
    public ChannelRow(ChannelView c)
    {
        Id = c.Id;
        Name = c.Name;
        Unit = c.Unit;
        Description = c.Description;
        HasRange = c.Min is not null || c.Max is not null;
        RangeText = HasRange
            ? $"{Format(c.Min) ?? "…"} .. {Format(c.Max) ?? "…"}"
            : "";
        Tooltip = string.IsNullOrEmpty(c.Description) ? c.Name : $"{c.Name}\n{c.Description}";
        Update(c);
    }

    public ushort Id { get; }
    public string Name { get; }
    public string Unit { get; }
    public string Description { get; }
    public string RangeText { get; }
    public bool HasRange { get; }
    public string Tooltip { get; }

    string _value = "—";
    public string Value { get => _value; private set => Set(ref _value, value); }

    bool _outOfRange;
    public bool OutOfRange { get => _outOfRange; private set => Set(ref _outOfRange, value); }

    double _fraction;
    /// <summary>Position of the value within its declared range, 0..1.</summary>
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    bool _stale = true;
    /// <summary>No value yet, or none for over two seconds.</summary>
    public bool IsStale { get => _stale; private set => Set(ref _stale, value); }

    public void Update(ChannelView c)
    {
        Value = c.Display ?? "—";
        OutOfRange = c.OutOfRange;
        IsStale = c.AgeSeconds is not double age || age > 2;
        if (c.Number is double n && c.Min is double lo && c.Max is double hi && hi > lo)
            Fraction = Math.Clamp((n - lo) / (hi - lo), 0, 1);
        else if (c.Number is double n2 && c.Max is double max && c.Min is null && max > 0)
            Fraction = Math.Clamp(n2 / max, 0, 1);
        else
            Fraction = 0;
    }

    static string? Format(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class LogRow(LogLine line)
{
    public long Seq { get; } = line.Seq;
    public string Time { get; } = line.At.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(9);
    public string Tag { get; } = line.Kind switch
    {
        LogKind.Discovery => "DISCOVERY",
        LogKind.Motion => "MOTION",
        LogKind.Event => "EVENT",
        _ => "CHECK",
    };
    public string Text { get; } = line.Text;
    public bool IsDiscovery => line.Kind == LogKind.Discovery;
    public bool IsMotion => line.Kind == LogKind.Motion;
    public bool IsEvent => line.Kind == LogKind.Event;
    public bool IsFinding => line.Kind == LogKind.Finding;
}

public sealed class EventRow(EventView e)
{
    public string Id { get; } = e.Id.ToString(CultureInfo.InvariantCulture);
    public string Name { get; } = e.Name;
    public string Intensity { get; } = e.Intensity.ToString("0.00", CultureInfo.InvariantCulture);
    public double IntensityFraction { get; } = Math.Clamp(e.Intensity, 0, 1);
    public string Duration { get; } = e.Duration > 0 ? e.Duration.ToString("0.00 s", CultureInfo.InvariantCulture) : "impulse";
    public string Direction { get; } = e.Direction is { } d
        ? string.Create(CultureInfo.InvariantCulture, $"({d.X:0.0}, {d.Y:0.0}, {d.Z:0.0})")
        : "omni";
    public string Object { get; } = e.Object.Length == 0 ? "primary" : e.Object;
    public string Copies { get; } = e.Copies > 1 ? $"×{e.Copies}" : "";
    public string Timestamp { get; } = e.Timestamp.ToString("0.00 s", CultureInfo.InvariantCulture);
}

public sealed class FindingRow(Finding f)
{
    public bool IsWarning { get; } = f.Severity == Severity.Warning;
    public string Level { get; } = f.Severity == Severity.Warning ? "warning" : "note";
    public string Rule { get; } = f.Rule;
    public string Message { get; } = f.Message;
    public string Count { get; } = f.Count > 1 ? $"×{f.Count}" : "";
    public string Age { get; } = f.AgeSeconds < 1 ? "now" : $"{f.AgeSeconds:0}s ago";
}
