namespace Ogs.Telemetry.Monitor;

/// <summary>
/// Aggregates conformance findings for <see cref="StreamTracker"/>. A finding
/// is keyed by rule + message, so a sender repeating the same mistake 60
/// times a second counts up one row instead of flooding the list; the first
/// occurrence of each is also written to the log. Called under the
/// tracker's lock.
/// </summary>
/// <remarks>
/// Rules (warnings unless noted), each traceable to the standard:
/// <list type="bullet">
/// <item><c>schema-version</c>: discovery schemaVersion is not 1.</item>
/// <item><c>gravity-magnitude</c>: gravity != |gravityVector| (CONVENTIONS, Gravity).</item>
/// <item><c>no-primary</c>: discovery declares no primary object.</item>
/// <item><c>duplicate-object</c>, <c>duplicate-channel-id</c>: names/ids must identify one thing.</item>
/// <item><c>quaternion-norm</c>: orientation is not a unit quaternion.</item>
/// <item><c>primary-first</c>: motion lists the primary object after another.</item>
/// <item><c>undeclared-object</c>, <c>undeclared-channel</c>: motion refers to names/ids discovery never declared.</item>
/// <item><c>event-id-order</c>: a new event id below one already seen (ids are session-monotonic).</item>
/// <item><c>event-intensity</c>: intensity outside 0..1 (C-3).</item>
/// <item><c>motion-before-discovery</c> (info): nothing to interpret motion against yet.</item>
/// <item><c>out-of-range</c> (info): a value outside its declared operating range.</item>
/// </list>
/// </remarks>
sealed class Conformance(StreamTracker owner)
{
    readonly Dictionary<(string Rule, string Message), Entry> _entries = new();

    public void Warn(string rule, string message, long now) => Add(Severity.Warning, rule, message, now);
    public void Info(string rule, string message, long now) => Add(Severity.Info, rule, message, now);

    void Add(Severity severity, string rule, string message, long now)
    {
        var key = (rule, message);
        if (_entries.TryGetValue(key, out var e))
        {
            e.Count++;
            e.Last = now;
            return;
        }
        _entries[key] = new Entry(severity, rule, message, now);
        owner.LogFinding(now, $"{(severity == Severity.Warning ? "warning" : "note")} [{rule}] {message}");
    }

    public void Clear() => _entries.Clear();

    public IReadOnlyList<Finding> View(long now) => _entries.Values
        .OrderByDescending(e => e.Severity)
        .ThenByDescending(e => e.Last)
        .Select(e => new Finding(e.Severity, e.Rule, e.Message, e.Count, owner.SecondsSince(now, e.Last)))
        .ToList();

    sealed class Entry(Severity severity, string rule, string message, long now)
    {
        public Severity Severity { get; } = severity;
        public string Rule { get; } = rule;
        public string Message { get; } = message;
        public long Count = 1;
        public long Last = now;
    }
}
