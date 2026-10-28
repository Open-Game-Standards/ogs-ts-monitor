using System.Diagnostics;
using System.Globalization;
using CapnpGen;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Ogs.Telemetry.Monitor;

/// <summary>
/// Folds a stream of decoded OGS-TS frames into a live picture of every
/// sender on the network, and checks each frame against the standard as it
/// goes. Feed it from <see cref="TelemetryReceiver"/>'s events (see
/// <see cref="Attach"/>) or raw datagrams via <see cref="Ingest"/>; read it
/// with <see cref="Snapshot"/>. Thread-safe: frames arrive on the receive
/// thread, snapshots are taken on the UI thread.
///
/// Sessions are keyed by discovery identity (game name + session start).
/// Motion and event frames carry no session id, so motion is attributed by
/// object name to the latest discovery that declared it — the same name in
/// two concurrent sessions resolves to the most recent declaration.
/// </summary>
public sealed class StreamTracker
{
    public const int MaxLogLines = 400;
    public const int MaxEvents = 200;
    /// <summary>How much motion history each object keeps for the time-history views.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(30);
    /// <summary>Motion log lines per object, at most one per this interval.</summary>
    public static readonly TimeSpan MotionLogInterval = TimeSpan.FromMilliseconds(500);

    readonly object _gate = new();
    readonly Func<long> _now;          // Stopwatch-style ticks
    readonly double _tickSeconds;
    readonly long _start;

    readonly Dictionary<(string Game, long Start), Session> _sessions = new();
    readonly Dictionary<string, (Session Session, ObjectState Obj)> _objectsByName = new();
    readonly LinkedList<LogLine> _log = new();
    long _logSeq;
    readonly Dictionary<uint, EventState> _eventsById = new();
    readonly LinkedList<EventState> _events = new();
    uint? _maxEventId;
    readonly Conformance _findings;

    long _discoveryFrames, _motionFrames, _eventFrames;
    readonly Queue<long> _motionArrivals = new();
    readonly List<Func<(long Foreign, long DecodeErrors)>> _receiverCounters = new();
    long _ingestForeign, _ingestDecodeErrors;

    public StreamTracker() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

    /// <summary>For tests: a controllable clock in ticks of
    /// <paramref name="ticksPerSecond"/>.</summary>
    public StreamTracker(Func<long> now, long ticksPerSecond)
    {
        _now = now;
        _tickSeconds = 1.0 / ticksPerSecond;
        _start = now();
        _findings = new Conformance(this);
    }

    double Seconds(long ticks) => ticks * _tickSeconds;
    double Uptime(long now) => Seconds(now - _start);

    /// <summary>Subscribes to a receiver's frames and adds its foreign/decode
    /// counters to the snapshot stats. Several receivers (say, two ports) can
    /// feed one tracker. Returns the tracker.</summary>
    public StreamTracker Attach(TelemetryReceiver receiver)
    {
        receiver.DiscoveryReceived += OnDiscovery;
        receiver.MotionReceived += OnMotion;
        receiver.EventReceived += OnEvent;
        lock (_gate) _receiverCounters.Add(() => (receiver.ForeignSkipped, receiver.DecodeErrors));
        return this;
    }

    /// <summary>Decodes one datagram (OMT1 envelope + Cap'n Proto message)
    /// and folds it in, exactly as a live packet would be. Returns false for
    /// foreign packets and decode errors (counted in the stats).</summary>
    public bool Ingest(ReadOnlyMemory<byte> datagram)
    {
        if (!Envelope.TryUnwrap(datagram, out var type, out var payload))
        {
            lock (_gate) _ingestForeign++;
            return false;
        }
        try
        {
            var root = MessageDecoder.DecodeRoot(payload.Span);
            switch (type)
            {
                case FrameType.Discovery: OnDiscovery(DiscoveryFrame.READER.create(root)); break;
                case FrameType.Motion: OnMotion(MotionFrame.READER.create(root)); break;
                case FrameType.Event: OnEvent(EventFrame.READER.create(root)); break;
                default: return true; // unknown frame types are skipped (evolution rules)
            }
            return true;
        }
        catch (Capnp.DeserializationException)
        {
            lock (_gate) _ingestDecodeErrors++;
            return false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessions.Clear();
            _objectsByName.Clear();
            _log.Clear();
            _eventsById.Clear();
            _events.Clear();
            _maxEventId = null;
            _findings.Clear();
            _discoveryFrames = _motionFrames = _eventFrames = 0;
            _motionArrivals.Clear();
        }
    }

    // ---- Discovery --------------------------------------------------------

    public void OnDiscovery(DiscoveryFrame.READER d)
    {
        long now = _now();
        lock (_gate)
        {
            _discoveryFrames++;
            string game = d.GameName ?? "";
            var key = (game, d.SessionStartUnixUs);
            bool isNew = !_sessions.TryGetValue(key, out var session);
            if (session is null)
            {
                session = new Session(game, d.SessionStartUnixUs);
                _sessions[key] = session;
            }
            session.DiscoveryCount++;
            session.LastDiscovery = now;
            session.SchemaVersion = d.SchemaVersion;
            var env = d.Environment;
            session.Medium = env.Medium.ToString();
            session.AirDensity = env.AirDensity;
            session.Temperature = env.Temperature;
            session.Pressure = env.Pressure;
            session.Gravity = Ogs.Telemetry.Gravity.Session(env);

            string who = $"'{game}'";
            if (d.SchemaVersion != 1)
                _findings.Warn("schema-version", $"{who} declares schemaVersion {d.SchemaVersion}; this monitor reads schema 1", now);
            if (env.which == CapnpGen.Environment.WHICH.GravityVector &&
                MathF.Abs(session.Gravity.Length() - env.Gravity) > 1e-3f * MathF.Max(1f, env.Gravity))
                _findings.Warn("gravity-magnitude",
                    $"{who} gravity {F(env.Gravity)} != |gravityVector| {F(session.Gravity.Length())}", now);

            var objects = d.Objects;
            if (!objects.Any(o => o.Primary))
                _findings.Warn("no-primary", $"{who} declares no primary object", now);

            var seen = new HashSet<string>();
            foreach (var o in objects)
            {
                string name = o.Name ?? "";
                if (!seen.Add(name))
                    _findings.Warn("duplicate-object", $"{who} declares object '{name}' more than once", now);
                if (!session.Objects.TryGetValue(name, out var obj))
                {
                    obj = new ObjectState(name);
                    session.Objects[name] = obj;
                }
                obj.Declared = true;
                obj.Type = o.Type.ToString();
                obj.TypeLabel = o.TypeLabel ?? "";
                obj.Location = o.Location ?? "";
                obj.Primary = o.Primary;
                obj.DrivePointCount = o.DrivePoints.Count;
                obj.SetChannels(o.Channels, out var duplicates);
                foreach (var id in duplicates)
                    _findings.Warn("duplicate-channel-id",
                        $"'{name}' declares channel id {id} more than once (its values can't be attributed)", now);
                foreach (var v in o.Values)
                    ApplyValue(obj, v, now, $"'{name}' discovery value");
                _objectsByName[name] = (session, obj);
            }

            Log(now, LogKind.Discovery,
                $"\"{game}\" · schema {d.SchemaVersion} · {Plural(objects.Count, "object")} · " +
                $"{Plural(objects.Sum(o => o.Channels.Count), "channel")} · {session.Medium}" +
                (isNew ? " · new session" : ""));
        }
    }

    // ---- Motion -----------------------------------------------------------

    public void OnMotion(MotionFrame.READER m)
    {
        long now = _now();
        lock (_gate)
        {
            _motionFrames++;
            _motionArrivals.Enqueue(now);
            TrimArrivals(now);

            var objects = m.Objects;
            if (objects.Count == 0) return;

            if (_sessions.Count == 0)
            {
                _findings.Info("motion-before-discovery",
                    "motion arriving before any discovery: channels and objects can't be identified yet", now);
            }

            // Primary first: if the frame carries a declared primary object it
            // must lead the list.
            int primaryIndex = -1;
            for (int i = 0; i < objects.Count; i++)
            {
                if (_objectsByName.TryGetValue(objects[i].Name ?? "", out var e) && e.Obj.Primary)
                {
                    primaryIndex = i;
                    break;
                }
            }
            if (primaryIndex > 0)
                _findings.Warn("primary-first",
                    $"motion frame lists primary '{objects[primaryIndex].Name}' at position {primaryIndex + 1}; the primary object goes first", now);

            foreach (var mo in objects)
            {
                string name = mo.Name ?? "";
                if (!_objectsByName.TryGetValue(name, out var entry))
                {
                    if (_sessions.Count > 0)
                        _findings.Warn("undeclared-object", $"motion for '{name}', which no discovery declares", now);
                    continue;
                }
                var (session, obj) = entry;
                obj.RecordArrival(now);
                obj.Timestamp = m.Timestamp;

                var q = mo.Orientation;
                var rot = new Quaternion(q.X, q.Y, q.Z, q.W);
                float norm = rot.Length();
                if (MathF.Abs(norm - 1f) > 1e-3f)
                    _findings.Warn("quaternion-norm", $"'{name}' orientation |q| = {norm:F4}, not a unit quaternion", now);
                var p = mo.Position;
                var (yaw, pitch, roll) = Euler.FromQuaternion(norm > 1e-6f ? Quaternion.Normalize(rot) : Quaternion.Identity);
                obj.Pose = new PoseView(new Vector3(p.X, p.Y, p.Z), rot, yaw, pitch, roll);

                obj.LocalGravity = mo.LocalGravity.which == MotionObject.localGravity.WHICH.Vector;
                obj.EffectiveGravity = Ogs.Telemetry.Gravity.Effective(mo, session.Gravity);

                obj.Dynamics = mo.which switch
                {
                    MotionObject.WHICH.BodyDynamics => new DynamicsView("bodyDynamics",
                        V(mo.BodyDynamics.SpecificForce), V(mo.BodyDynamics.AngularVelocity)),
                    MotionObject.WHICH.Kinematics => new DynamicsView("kinematics",
                        Ogs.Telemetry.Gravity.SpecificForce(V(mo.Kinematics.Acceleration),
                            norm > 1e-6f ? Quaternion.Normalize(rot) : Quaternion.Identity, obj.EffectiveGravity),
                        null),
                    _ => null,
                };

                foreach (var cv in mo.Channels)
                    ApplyValue(obj, cv, now, $"'{name}' motion");

                double at = Uptime(now);
                obj.History.Enqueue(new MotionSample(at, obj.Pose.Position, rot, yaw, pitch, roll,
                    obj.Dynamics?.SpecificForce, obj.Dynamics?.AngularVelocity));
                while (obj.History.Count > 0 && obj.History.Peek().At < at - HistoryWindow.TotalSeconds)
                    obj.History.Dequeue();

                if (obj.LastMotionLog is not long last || now - last >= MotionLogInterval.TotalSeconds / _tickSeconds)
                {
                    obj.LastMotionLog = now;
                    Log(now, LogKind.Motion, MotionLine(m.Timestamp, obj));
                }
            }
        }
    }

    // ---- Events -----------------------------------------------------------

    public void OnEvent(EventFrame.READER e)
    {
        long now = _now();
        lock (_gate)
        {
            _eventFrames++;
            foreach (var ev in e.Events)
            {
                if (_eventsById.TryGetValue(ev.Id, out var known))
                {
                    known.Copies++; // a replay for redundancy: deduplicated
                    continue;
                }
                if (_maxEventId is uint max && ev.Id < max)
                    _findings.Warn("event-id-order",
                        $"new event id {ev.Id} ('{ev.Name}') is below the highest seen ({max}); ids are session-monotonic", now);
                _maxEventId = _maxEventId is uint m2 ? Math.Max(m2, ev.Id) : ev.Id;
                if (ev.Intensity < 0f || ev.Intensity > 1f || float.IsNaN(ev.Intensity))
                    _findings.Warn("event-intensity", $"event '{ev.Name}' intensity {F(ev.Intensity)} is outside 0..1", now);

                Vector3? dir = ev.which == Event.WHICH.Direction ? V(ev.Direction) : null;
                var state = new EventState(ev.Id, ev.Name ?? "", ev.Intensity, ev.Duration, dir,
                    ev.Object ?? "", e.Timestamp, now);
                _eventsById[ev.Id] = state;
                _events.AddLast(state);
                while (_events.Count > MaxEvents)
                {
                    _eventsById.Remove(_events.First!.Value.Id);
                    _events.RemoveFirst();
                }
                Log(now, LogKind.Event,
                    $"{state.Name} · intensity {state.Intensity:F2}" +
                    (state.Duration > 0 ? $" · {state.Duration:F2} s" : "") +
                    (dir is Vector3 d ? $" · dir ({d.X:F1}, {d.Y:F1}, {d.Z:F1})" : "") +
                    $" · id {state.Id}" + (state.Object.Length > 0 ? $" · '{state.Object}'" : ""));
            }
        }
    }

    // ---- Snapshot ---------------------------------------------------------

    public MonitorSnapshot Snapshot()
    {
        long now = _now();
        lock (_gate)
        {
            TrimArrivals(now);
            var sessions = _sessions.Values
                .OrderByDescending(s => s.LastDiscovery)
                .Select(s => new SessionView(
                    s.Game, s.Start, s.SchemaVersion, s.Medium, s.Gravity,
                    s.AirDensity, s.Temperature, s.Pressure,
                    Seconds(now - s.LastDiscovery), s.DiscoveryCount,
                    s.Objects.Values
                        .OrderByDescending(o => o.Primary)
                        .ThenBy(o => o.Name, StringComparer.Ordinal)
                        .Select(o => o.View(s, now, Seconds))
                        .ToList()))
                .ToList();
            var events = _events.Reverse()
                .Select(ev => new EventView(ev.Id, ev.Name, ev.Intensity, ev.Duration, ev.Direction,
                    ev.Object, ev.Timestamp, ev.Copies, Seconds(now - ev.Arrived)))
                .ToList();
            long foreign = 0, decodeErrors = 0;
            foreach (var counters in _receiverCounters)
            {
                var (f, d) = counters();
                foreign += f;
                decodeErrors += d;
            }
            var stats = new StreamStats(_discoveryFrames, _motionFrames, _eventFrames,
                _motionArrivals.Count, // arrivals in the last second
                foreign + _ingestForeign, decodeErrors + _ingestDecodeErrors, Uptime(now));
            return new MonitorSnapshot(sessions, _log.ToList(), events, _findings.View(now), stats);
        }
    }

    /// <summary>The last <see cref="HistoryWindow"/> of motion for one object
    /// (by <see cref="ObjectView.Key"/>), oldest first; empty if unknown.</summary>
    public IReadOnlyList<MotionSample> History(string objectKey)
    {
        lock (_gate)
        {
            foreach (var s in _sessions.Values)
                foreach (var o in s.Objects.Values)
                    if (ObjectState.KeyOf(s, o.Name) == objectKey) return o.History.ToArray();
            return Array.Empty<MotionSample>();
        }
    }

    /// <summary>Monitor uptime in seconds: the time axis of <see cref="MotionSample.At"/>.</summary>
    public double Now() => Uptime(_now());

    // ---- Internals --------------------------------------------------------

    void ApplyValue(ObjectState obj, ChannelValue.READER v, long now, string where)
    {
        if (!obj.Channels.TryGetValue(v.Id, out var ch))
        {
            _findings.Warn("undeclared-channel", $"{where} carries channel id {v.Id}, which '{obj.Name}' doesn't declare", now);
            return;
        }
        ch.Updated = now;
        switch (v.which)
        {
            case ChannelValue.WHICH.Number:
                ch.Number = v.Number;
                ch.Display = v.Number.ToString("0.###", CultureInfo.InvariantCulture);
                ch.OutOfRange = (ch.Min is double lo && v.Number < lo) || (ch.Max is double hi && v.Number > hi);
                if (ch.OutOfRange)
                    _findings.Info("out-of-range",
                        $"'{obj.Name}' {ch.Name} = {ch.Display} is outside its declared range {Range(ch)}", now);
                break;
            case ChannelValue.WHICH.Boolean:
                ch.Number = null;
                ch.Display = v.Boolean ? "true" : "false";
                ch.OutOfRange = false;
                break;
            case ChannelValue.WHICH.Text:
                ch.Number = null;
                ch.Display = v.Text ?? "";
                ch.OutOfRange = false;
                break;
        }
    }

    static string MotionLine(double timestamp, ObjectState obj)
    {
        var parts = new List<string> { $"t {timestamp:F2} s", $"'{obj.Name}'" };
        if (obj.Pose is PoseView p)
            parts.Add($"yaw {Signed(p.Yaw)} pitch {Signed(p.Pitch)} roll {Signed(p.Roll)}");
        foreach (var ch in obj.Channels.Values.Where(c => c.Display is not null).Take(3))
            parts.Add($"{ch.Name} {ch.Display}");
        return string.Join(" · ", parts);
    }

    void Log(long now, LogKind kind, string text)
    {
        _log.AddLast(new LogLine(++_logSeq, Uptime(now), kind, text));
        while (_log.Count > MaxLogLines) _log.RemoveFirst();
    }

    void TrimArrivals(long now)
    {
        long horizon = now - (long)(1.0 / _tickSeconds);
        while (_motionArrivals.Count > 0 && _motionArrivals.Peek() < horizon) _motionArrivals.Dequeue();
    }

    internal void LogFinding(long now, string text) => Log(now, LogKind.Finding, text);
    internal double SecondsSince(long now, long then) => Seconds(now - then);

    static Vector3 V(CapnpGen.Vector3.READER v) => new(v.X, v.Y, v.Z);
    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Signed(float v) => (v >= 0 ? "+" : "") + (v + 0f).ToString("F1", CultureInfo.InvariantCulture);
    static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
    static string Range(ChannelState c) =>
        $"{(c.Min?.ToString(CultureInfo.InvariantCulture) ?? "…")}..{(c.Max?.ToString(CultureInfo.InvariantCulture) ?? "…")}";

    // ---- State ------------------------------------------------------------

    sealed class Session(string game, long start)
    {
        public string Game { get; } = game;
        public long Start { get; } = start;
        public ushort SchemaVersion;
        public string Medium = "";
        public float AirDensity, Temperature, Pressure;
        public Vector3 Gravity;
        public long LastDiscovery;
        public long DiscoveryCount;
        public readonly Dictionary<string, ObjectState> Objects = new(StringComparer.Ordinal);
    }

    sealed class ObjectState(string name)
    {
        const int RateWindow = 120;
        public string Name { get; } = name;
        public bool Declared;
        public string Type = "", TypeLabel = "", Location = "";
        public bool Primary;
        public int DrivePointCount;
        public readonly Dictionary<ushort, ChannelState> Channels = new();
        public PoseView? Pose;
        public DynamicsView? Dynamics;
        public Vector3 EffectiveGravity;
        public bool LocalGravity;
        public double? Timestamp;
        public long? LastMotionLog;
        public readonly Queue<MotionSample> History = new();

        public static string KeyOf(Session s, string name) => $"{s.Game}\u001f{s.Start}\u001f{name}";
        readonly Queue<long> _arrivals = new();

        public void RecordArrival(long now)
        {
            _arrivals.Enqueue(now);
            while (_arrivals.Count > RateWindow) _arrivals.Dequeue();
        }

        public void SetChannels(IReadOnlyList<ChannelDescriptor.READER> declared, out List<ushort> duplicates)
        {
            duplicates = new List<ushort>();
            var ids = new HashSet<ushort>();
            foreach (var d in declared)
            {
                if (!ids.Add(d.Id)) { duplicates.Add(d.Id); continue; }
                if (!Channels.TryGetValue(d.Id, out var ch))
                {
                    ch = new ChannelState(d.Id);
                    Channels[d.Id] = ch;
                }
                ch.Name = d.Name ?? "";
                ch.Unit = d.Unit ?? "";
                ch.Description = d.Description ?? "";
                bool ranged = d.which == ChannelDescriptor.WHICH.Range;
                ch.Min = ranged ? d.Range.Min : null;
                ch.Max = ranged ? d.Range.Max : null;
            }
            // channels dropped from the declaration go with it
            foreach (var stale in Channels.Keys.Where(k => !ids.Contains(k)).ToList())
                Channels.Remove(stale);
        }

        public ObjectView View(Session s, long now, Func<long, double> seconds)
        {
            double rate = 0, age = double.PositiveInfinity;
            if (_arrivals.Count > 0)
            {
                long last = _arrivals.Last();
                age = seconds(now - last);
                if (_arrivals.Count > 1)
                {
                    double span = seconds(last - _arrivals.Peek());
                    if (span > 0) rate = (_arrivals.Count - 1) / span;
                }
                if (age > 2) rate = 0; // stalled
            }
            return new ObjectView(
                KeyOf(s, Name), Name, Type, TypeLabel, Location, Primary,
                DrivePointCount, rate, age, Timestamp, Pose, Dynamics,
                Pose is null ? s.Gravity : EffectiveGravity, LocalGravity,
                Channels.Values.OrderBy(c => c.Id).Select(c => new ChannelView(
                    c.Id, c.Name, c.Unit, c.Min, c.Max, c.Description, c.Display, c.Number,
                    c.OutOfRange, c.Updated is long u ? seconds(now - u) : null)).ToList());
        }
    }

    sealed class ChannelState(ushort id)
    {
        public ushort Id { get; } = id;
        public string Name = "", Unit = "", Description = "";
        public double? Min, Max;
        public string? Display;
        public double? Number;
        public bool OutOfRange;
        public long? Updated;
    }

    sealed class EventState(uint id, string name, float intensity, float duration, Vector3? direction,
                             string obj, double timestamp, long arrived)
    {
        public uint Id { get; } = id;
        public string Name { get; } = name;
        public float Intensity { get; } = intensity;
        public float Duration { get; } = duration;
        public Vector3? Direction { get; } = direction;
        public string Object { get; } = obj;
        public double Timestamp { get; } = timestamp;
        public long Arrived { get; } = arrived;
        public int Copies = 1;
    }
}
