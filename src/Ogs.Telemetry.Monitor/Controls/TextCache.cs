using System.Globalization;
using Avalonia.Media;

namespace Ogs.Telemetry.Monitor;

/// <summary>
/// Shaped text for the custom-drawn views. Building a <see cref="FormattedText"/>
/// shapes and lays out the string, which is the costly part of a draw; the views
/// redraw ~30 times a second and mostly draw the same labels (ladder numbers,
/// tape marks, axis names), so the result is kept and reused. A
/// <see cref="FormattedText"/> is not changed by drawing it, so sharing one is safe.
///
/// UI thread only (the views draw there). Bounded: a readout that changes every
/// frame would otherwise grow it without end, so it is emptied when full.
/// </summary>
public static class TextCache
{
    const int Limit = 512;

    readonly record struct Key(string Text, double Size, Typeface Face, uint Colour, double Opacity, IBrush? Other);

    static readonly Dictionary<Key, FormattedText> Cache = new();

    public static FormattedText Get(string text, double size, Typeface face, IBrush brush)
    {
        // brushes made fresh each frame (same colour, new object) must still hit
        var key = brush is ISolidColorBrush s
            ? new Key(text, size, face, s.Color.ToUInt32(), s.Opacity, null)
            : new Key(text, size, face, 0, 0, brush);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        if (Cache.Count >= Limit) Cache.Clear();
        return Cache[key] = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, face, size, brush);
    }
}
