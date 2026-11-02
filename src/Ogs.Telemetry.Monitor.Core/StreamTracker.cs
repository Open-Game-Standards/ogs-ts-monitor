using System.Diagnostics;
using System.Globalization;
using System.Net;
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
/// Senders are told apart by source address and port (implementation-udp.md
/// § Senders and sessions): each has its own session, objects, descriptor
/// tables and event dedup, and motion and events belong to the session of
/// their source. A discovery frame with a new game name or session start
/// from the same source starts a new session. Frames fed without a source
/// (<see cref="Ingest"/> with none) count as one unattributed sender.
///
/// Paged discovery and motion parts (implementation-udp.md § Datagram
/// budget): pages of a session's current generation merge by object name
/// and channel id, an object's identity coming from the lowest page that
/// lists it; channels drop out only once a generation is complete. Pose,
/// dynamics and rate come from the first part listing an object in a
/// tick; channel values apply only when the motion frame names the
/// session's current generation, and are held back while that
/// generation's pages are still arriving.
/// </summary>
public sealed class StreamTracker
{
    public const int MaxLogLines = 400;
    public const int MaxEvents = 200;
    /// <summary>The standard's datagram budget, envelope included.</summary>
    public const int DatagramBudget = 1400;
    /// <summary>How much motion history each object keeps for the time-history views.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(30);
    /// <summary>Motion log lines per object, at most one per this interval.</summary>
    public static readonly TimeSpan MotionLogInterval = TimeSpan.FromMilliseconds(500);

    readonly object _gate = new();
    readonly Func<long> _now;          // Stopwatch-style ticks
    readonly double _tickSeconds;
    readonly long _start;

    /// <summary>Source of frames fed without one.</summary>
    public static readonly IPEndPoint Unattributed = new(IPAddress.None, 0);
    /// <summary>The standard's sender size envelope (implementation-udp.md
    /// § Sender size envelope): total bytes per second, and discovery bytes
    /// per generation.</summary>
    public const double EnvelopeBytesPerSecond = 105_000;
    public const int EnvelopeDiscoveryBytes = 30_000;
    static readonly TimeSpan EnvelopeWindow = TimeSpan.FromSeconds(5);

    readonly Dictionary<IPEndPoint, Session> _sessions = new();
    readonly LinkedList<LogLine> _log = new();
    long _logSeq;
    readonly Dictionary<(IPEndPoint, uint), EventState> _eventsById = new();
    readonly LinkedList<EventState> _events = new();
    readonly Dictionary<IPEndPoint, uint> _maxEventId = new();
    readonly Dictionary<IPEndPoint, Queue<(long At, int Bytes)>> _bytes = new();
    readonly Dictionary<IPEndPoint, int> _lastDiscoveryBytes = new();
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
        receiver.DiscoveryReceivedFrom += (src, d) => OnDiscovery(d, src);
        receiver.MotionReceivedFrom += (src, m) => OnMotion(m, src);
        receiver.EventReceivedFrom += (src, e) => OnEvent(e, src);
        receiver.DatagramReceivedFrom += (src, type, length) => OnDatagram(type, length, src);
        lock (_gate) _receiverCounters.Add(() => (receiver.ForeignSkipped, receiver.DecodeErrors));
        return this;
    }

    /// <summary>Decodes one datagram (OMT1 envelope + Cap'n Proto message)
    /// and folds it in, exactly as a live packet would be. Returns false for
    /// foreign packets and decode errors (counted in the stats).</summary>
    public bool Ingest(ReadOnlyMemory<byte> datagram, IPEndPoint? source = null)
    {
        if (!Envelope.TryUnwrap(datagram, out var type, out var payload))
        {
            lock (_gate) _ingestForeign++;
            return false;
        }
        OnDatagram(type, datagram.Length, source);
        try
        {
            var root = MessageDecoder.DecodeRoot(payload.Span);
            switch (type)
            {
                case FrameType.Discovery: OnDiscovery(DiscoveryFrame.READER.create(root), source); break;
                case FrameType.Motion: OnMotion(MotionFrame.READER.create(root), source); break;
                case FrameType.Event: OnEvent(EventFrame.READER.create(root), source); break;
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

    /// <summary>Checks one datagram's size against the 1,400-byte budget
    /// (a MUST). <see cref="Attach"/> wires it to the receiver;
    /// <see cref="Ingest"/> calls it itself.</summary>
    public void OnDatagram(FrameType type, int length, IPEndPoint? source = null)
    {
        long now = _now();
        var src = source ?? Unattributed;
        lock (_gate)
        {
            if (length > DatagramBudget)
                _findings.Warn("datagram-budget",
                    $"{type.ToString().ToLowerInvariant()} datagram of {length} bytes exceeds the " +
                    $"{DatagramBudget}-byte budget (page discovery, split motion into parts)", now);
            if (type == FrameType.Discovery) _lastDiscoveryBytes[src] = length;
            CheckEnvelope(src, length, now);
        }
    }

    /// <summary>The sender size envelope is a SHOULD: a sender above it is
    /// reported as information, averaged over a few seconds.</summary>
    void CheckEnvelope(IPEndPoint source, int length, long now)
    {
        if (!_bytes.TryGetValue(source, out var q)) _bytes[source] = q = new();
        q.Enqueue((now, length));
        long horizon = now - (long)(EnvelopeWindow.TotalSeconds / _tickSeconds);
        while (q.Count > 0 && q.Peek().At < horizon) q.Dequeue();
        if (q.Count < 2) return;
        double span = Seconds(now - q.Peek().At);
        if (span < EnvelopeWindow.TotalSeconds * 0.8) return; // not a full window yet
        double rate = q.Sum(e => (double)e.Bytes) / span;
        if (rate > EnvelopeBytesPerSecond)
            _findings.Info("size-envelope",
                $"{Who(source)} sends {rate / 1000:0} KB/s, above the ~{EnvelopeBytesPerSecond / 1000:0} KB/s " +
                "sender size envelope (implementation-udp.md § Sender size envelope)", now);
    }

    string Who(IPEndPoint source) =>
        _sessions.TryGetValue(source, out var s) ? s.Who
        : ReferenceEquals(source, Unattributed) ? "the sender" : source.ToString();

    public void Clear()
    {
        lock (_gate)
        {
            _sessions.Clear();
            _log.Clear();
            _eventsById.Clear();
            _events.Clear();
            _maxEventId.Clear();
            _bytes.Clear();
            _lastDiscoveryBytes.Clear();
            _findings.Clear();
            _discoveryFrames = _motionFrames = _eventFrames = 0;
            _motionArrivals.Clear();
        }
    }

    // ---- Discovery --------------------------------------------------------

    public void OnDiscovery(DiscoveryFrame.READER d, IPEndPoint? source = null)
    {
        long now = _now();
        var src = source ?? Unattributed;
        lock (_gate)
        {
            _discoveryFrames++;
            string game = d.GameName ?? "";
            _sessions.TryGetValue(src, out var session);
            // a new game or session start from the same source is a new session
            bool isNew = session is null || session.Game != game || session.Start != d.SessionStartUnixUs;
            if (isNew)
            {
                session = new Session(src, game, d.SessionStartUnixUs);
                _sessions[src] = session;
                _maxEventId.Remove(src);
            }
            session!.InstanceLabel = d.InstanceLabel ?? "";
            session.Host = d.Host ?? "";
            session.DiscoveryCount++;
            session.LastDiscovery = now;
            session.SchemaVersion = d.SchemaVersion;
            var env = d.Environment;
            session.Medium = env.Medium.ToString();
            session.AirDensity = env.AirDensity;
            session.Temperature = env.Temperature;
            session.Pressure = env.Pressure;
            session.Gravity = Ogs.Telemetry.Gravity.Session(env);

            string who = session.Who;
            bool paged = d.PageCount > 1;
            int page = paged ? d.Page : 0;
            if (paged && d.Page >= d.PageCount)
                _findings.Warn("page-index", $"{who} sends discovery page {d.Page} of {d.PageCount}", now);

            bool newGeneration = !session.HasGeneration || d.Generation != session.Generation;

            // descriptors may change only under a new generation (a MUST)
            string signature = Signature(d);
            if (!newGeneration && session.PageSignatures.TryGetValue(page, out var previous) &&
                previous != signature)
                _findings.Warn("generation-not-bumped",
                    $"{who} changed its descriptors without a new generation " +
                    $"(generation {d.Generation}{(paged ? $", page {page}" : "")})", now);

            if (newGeneration)
            {
                if (session.HasGeneration && d.Generation != unchecked(session.Generation + 1))
                    _findings.Info("generation-step",
                        $"{who} moved from generation {session.Generation} to {d.Generation} " +
                        $"(generations increment by one: discovery missed, or the sender restarted)", now);
                session.Generation = d.Generation;
                session.HasGeneration = true;
                session.StartGeneration();
            }
            else if (!paged) session.StartGeneration(); // an unpaged frame is the complete set
            session.PageCount = paged ? d.PageCount : 1;
            session.PageSignatures[page] = signature;
            session.PagesSeen.Add(page);
            if (_lastDiscoveryBytes.TryGetValue(src, out var bytes)) session.PageBytes[page] = bytes;

            if (d.SchemaVersion != 1)
                _findings.Warn("schema-version", $"{who} declares schemaVersion {d.SchemaVersion}; this monitor reads schema 1", now);
            if (env.which == CapnpGen.Environment.WHICH.GravityVector &&
                MathF.Abs(session.Gravity.Length() - env.Gravity) > 1e-3f * MathF.Max(1f, env.Gravity))
                _findings.Warn("gravity-magnitude",
                    $"{who} gravity {F(env.Gravity)} != |gravityVector| {F(session.Gravity.Length())}", now);

            var objects = d.Objects;
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
                if (page <= obj.IdentityPage)
                {
                    // the lowest-numbered page listing an object carries its identity
                    obj.IdentityPage = page;
                    obj.Type = o.Type.ToString();
                    obj.TypeLabel = o.TypeLabel ?? "";
                    obj.Location = o.Location ?? "";
                    obj.Primary = o.Primary;
                    obj.DrivePointCount = o.DrivePoints.Count;
                }
                obj.MergeChannels(o.Channels, page, out var duplicates);
                foreach (var id in duplicates)
                    _findings.Warn("duplicate-channel-id",
                        $"'{name}' declares channel id {id} more than once (its values can't be attributed)", now);
                foreach (var v in o.Values)
                    ApplyValue(obj, v, now, $"'{name}' discovery value");
            }

            bool complete = session.Complete;
            if (complete)
            {
                // the whole set is known: channels it no longer declares go,
                // and exactly one primary object should be among its objects
                foreach (var obj in session.Objects.Values) obj.DropUndeclared();
                if (!session.Objects.Values.Any(o => o.Primary && o.IdentityPage != int.MaxValue))
                    _findings.Warn("no-primary", $"{who} declares no primary object", now);
                int total = session.PageBytes.Values.Sum();
                if (total > EnvelopeDiscoveryBytes)
                    _findings.Info("size-envelope",
                        $"{who} declares {total / 1000.0:0.#} KB of discovery, above the ~{EnvelopeDiscoveryBytes / 1000} KB " +
                        "sender size envelope (implementation-udp.md § Sender size envelope)", now);
            }

            Log(now, LogKind.Discovery,
                $"{who} · schema {d.SchemaVersion} · {Plural(objects.Count, "object")} · " +
                $"{Plural(objects.Sum(o => o.Channels.Count), "channel")} · {session.Medium}" +
                $" · generation {d.Generation}" +
                (paged ? $" · page {d.Page + 1}/{d.PageCount}{(complete ? " · complete" : "")}" : "") +
                (isNew ? " · new session" : ""));
        }
    }

    // ---- Motion -----------------------------------------------------------

    public void OnMotion(MotionFrame.READER m, IPEndPoint? source = null)
    {
        long now = _now();
        var src = source ?? Unattributed;
        lock (_gate)
        {
            _motionFrames++;
            bool parted = m.PartCount > 1;
            if (parted && m.Part >= m.PartCount)
                _findings.Warn("part-index", $"motion part {m.Part} of {m.PartCount}", now);
            // a tick counts once, however many parts carry it
            if (m.Part == 0) _motionArrivals.Enqueue(now);
            TrimArrivals(now);

            var objects = m.Objects;
            if (objects.Count == 0) return;

            // motion belongs to the session of its source
            if (!_sessions.TryGetValue(src, out var session))
            {
                _findings.Info("motion-before-discovery",
                    $"motion from {Who(src)} arriving before its discovery: channels and objects can't be identified yet", now);
                return;
            }

            // Primary first: if the frame carries a declared primary object it
            // must lead the list.
            int primaryIndex = -1;
            for (int i = 0; i < objects.Count; i++)
            {
                if (session.Objects.TryGetValue(objects[i].Name ?? "", out var e) && e.Primary)
                {
                    primaryIndex = i;
                    break;
                }
            }
            if (primaryIndex > 0 && m.Part == 0)
                _findings.Warn("primary-first",
                    $"motion frame lists primary '{objects[primaryIndex].Name}' at position {primaryIndex + 1}; the primary object goes first", now);

            foreach (var mo in objects)
            {
                string name = mo.Name ?? "";
                if (!session.Objects.TryGetValue(name, out var obj) || !obj.Declared)
                {
                    _findings.Warn("undeclared-object", $"{session.Who} sends motion for '{name}', which its discovery doesn't declare", now);
                    continue;
                }

                // channel values: only for the generation the tables describe;
                // held back while that generation's pages are still arriving
                bool current = m.Generation == session.Generation;
                if (current)
                    foreach (var cv in mo.Channels)
                        ApplyValue(obj, cv, now, $"'{name}' motion", holdIfUnknown: !session.Complete);

                // pose, dynamics and rate ride the first part listing the object
                // in a tick; a later part carries channel values only
                if (parted && obj.PoseTick == m.Timestamp) continue;
                obj.PoseTick = m.Timestamp;
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

    public void OnEvent(EventFrame.READER e, IPEndPoint? source = null)
    {
        long now = _now();
        var src = source ?? Unattributed;
        lock (_gate)
        {
            _eventFrames++;
            foreach (var ev in e.Events)
            {
                // ids are session-monotonic per sender: dedup per source
                if (_eventsById.TryGetValue((src, ev.Id), out var known))
                {
                    known.Copies++; // a replay for redundancy: deduplicated
                    continue;
                }
                if (_maxEventId.TryGetValue(src, out var max) && ev.Id < max)
                    _findings.Warn("event-id-order",
                        $"new event id {ev.Id} ('{ev.Name}') is below the highest seen ({max}); ids are session-monotonic", now);
                _maxEventId[src] = _maxEventId.TryGetValue(src, out var m2) ? Math.Max(m2, ev.Id) : ev.Id;
                if (ev.Intensity < 0f || ev.Intensity > 1f || float.IsNaN(ev.Intensity))
                    _findings.Warn("event-intensity", $"event '{ev.Name}' intensity {F(ev.Intensity)} is outside 0..1", now);

                Vector3? dir = ev.which == Event.WHICH.Direction ? V(ev.Direction) : null;
                var state = new EventState(src, ev.Id, ev.Name ?? "", ev.Intensity, ev.Duration, dir,
                    ev.Object ?? "", e.Timestamp, now);
                _eventsById[(src, ev.Id)] = state;
                _events.AddLast(state);
                while (_events.Count > MaxEvents)
                {
                    var first = _events.First!.Value;
                    _eventsById.Remove((first.Source, first.Id));
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
                    s.Generation, s.PageCount, s.PagesSeen.Count,
                    s.Objects.Values
                        .OrderByDescending(o => o.Primary)
                        .ThenBy(o => o.Name, StringComparer.Ordinal)
                        .Select(o => o.View(s, now, Seconds))
                        .ToList(),
                    s.InstanceLabel, s.Host,
                    ReferenceEquals(s.Source, Unattributed) ? "" : s.Source.ToString()))
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

    void ApplyValue(ObjectState obj, ChannelValue.READER v, long now, string where, bool holdIfUnknown = false)
    {
        if (!obj.Channels.TryGetValue(v.Id, out var ch))
        {
            // the id's page has not arrived yet: hold the value back
            if (holdIfUnknown) return;
            _findings.Warn("undeclared-channel", $"{where} carries channel id {v.Id}, which '{obj.Name}' doesn't declare", now);
            return;
        }
        bool matches = ch.Type switch
        {
            ChannelType.number => v.which == ChannelValue.WHICH.Number,
            ChannelType.boolean => v.which == ChannelValue.WHICH.Boolean,
            ChannelType.text => v.which == ChannelValue.WHICH.Text,
            _ => true,
        };
        if (!matches)
        {
            // a value whose variant differs from the declared type is dropped
            _findings.Warn("channel-type",
                $"{where} sends {ch.Name} as {v.which.ToString().ToLowerInvariant()}, but it is declared {ch.Type}", now);
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

    /// <summary>A page's descriptors (session values excluded: they may
    /// change within a generation).</summary>
    static string Signature(DiscoveryFrame.READER d)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var o in d.Objects)
        {
            sb.Append(o.Name).Append('\u001e').Append(o.Type).Append('\u001e').Append(o.TypeLabel)
              .Append('\u001e').Append(o.Location).Append('\u001e').Append(o.Primary)
              .Append('\u001e').Append(o.DrivePoints.Count).Append('\u001e').Append(o.ReferencePoint.Name);
            foreach (var c in o.Channels)
            {
                sb.Append('\u001f').Append(c.Id).Append('|').Append(c.Name).Append('|').Append(c.Unit)
                  .Append('|').Append(c.Description).Append('|').Append(c.Type);
                if (c.which == ChannelDescriptor.WHICH.Range)
                    sb.Append('|').Append(c.Range.Min.ToString(CultureInfo.InvariantCulture))
                      .Append('|').Append(c.Range.Max.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('\u001d');
        }
        return sb.ToString();
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

    sealed class Session(IPEndPoint source, string game, long start)
    {
        public IPEndPoint Source { get; } = source;
        public string Game { get; } = game;
        public long Start { get; } = start;
        public string InstanceLabel = "", Host = "";
        /// <summary>The sender as findings name it: game, then instance label.</summary>
        public string Who => InstanceLabel.Length > 0 ? $"'{Game}' ({InstanceLabel})" : $"'{Game}'";
        /// <summary>Datagram size of each page of the current generation.</summary>
        public readonly Dictionary<int, int> PageBytes = new();
        public ushort SchemaVersion;
        public string Medium = "";
        public float AirDensity, Temperature, Pressure;
        public Vector3 Gravity;
        public long LastDiscovery;
        public long DiscoveryCount;
        public readonly Dictionary<string, ObjectState> Objects = new(StringComparer.Ordinal);

        // the current descriptor-set generation and its pages
        public uint Generation;
        public bool HasGeneration;
        public int PageCount = 1;
        public readonly HashSet<int> PagesSeen = new();
        public readonly Dictionary<int, string> PageSignatures = new();
        public bool Complete => PagesSeen.Count >= PageCount;

        /// <summary>Starts collecting a descriptor set afresh: pages, the
        /// identity page and the declared channels of every object.</summary>
        public void StartGeneration()
        {
            PagesSeen.Clear();
            PageSignatures.Clear();
            PageBytes.Clear();
            foreach (var o in Objects.Values) o.StartGeneration();
        }
    }

    sealed class ObjectState(string name)
    {
        const int RateWindow = 120;
        public string Name { get; } = name;
        public bool Declared;
        /// <summary>Page this generation's identity fields came from; MaxValue = none yet.</summary>
        public int IdentityPage = int.MaxValue;
        /// <summary>Channel ids this generation declares, with the page declaring each.</summary>
        readonly Dictionary<ushort, int> _declaredOn = new();
        /// <summary>Timestamp of the tick whose pose was last taken (motion parts).</summary>
        public double? PoseTick;
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

        public static string KeyOf(Session s, string name) => $"{s.Source}\u001f{s.Game}\u001f{s.Start}\u001f{name}";
        readonly Queue<long> _arrivals = new();

        public void RecordArrival(long now)
        {
            _arrivals.Enqueue(now);
            while (_arrivals.Count > RateWindow) _arrivals.Dequeue();
        }

        public void StartGeneration()
        {
            IdentityPage = int.MaxValue;
            _declaredOn.Clear();
        }

        /// <summary>Adds or updates the channels one page declares. An id
        /// declared twice in a generation (on one page or two) is reported.</summary>
        public void MergeChannels(IReadOnlyList<ChannelDescriptor.READER> declared, int page, out List<ushort> duplicates)
        {
            duplicates = new List<ushort>();
            var onThisPage = new HashSet<ushort>();
            foreach (var d in declared)
            {
                if (!onThisPage.Add(d.Id) ||
                    (_declaredOn.TryGetValue(d.Id, out var otherPage) && otherPage != page))
                {
                    duplicates.Add(d.Id);
                    continue;
                }
                _declaredOn[d.Id] = page;
                if (!Channels.TryGetValue(d.Id, out var ch))
                {
                    ch = new ChannelState(d.Id);
                    Channels[d.Id] = ch;
                }
                ch.Name = d.Name ?? "";
                ch.Unit = d.Unit ?? "";
                ch.Description = d.Description ?? "";
                ch.Type = d.Type;
                bool ranged = d.which == ChannelDescriptor.WHICH.Range;
                ch.Min = ranged ? d.Range.Min : null;
                ch.Max = ranged ? d.Range.Max : null;
            }
        }

        /// <summary>Once a generation is complete: channels it no longer
        /// declares go with the declaration.</summary>
        public void DropUndeclared()
        {
            foreach (var stale in Channels.Keys.Where(k => !_declaredOn.ContainsKey(k)).ToList())
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
                    c.OutOfRange, c.Updated is long u ? seconds(now - u) : null,
                    c.Type == ChannelType.unspecified ? "" : c.Type.ToString())).ToList());
        }
    }

    sealed class ChannelState(ushort id)
    {
        public ushort Id { get; } = id;
        public string Name = "", Unit = "", Description = "";
        public ChannelType Type;
        public double? Min, Max;
        public string? Display;
        public double? Number;
        public bool OutOfRange;
        public long? Updated;
    }

    sealed class EventState(IPEndPoint source, uint id, string name, float intensity, float duration,
                             Vector3? direction, string obj, double timestamp, long arrived)
    {
        public IPEndPoint Source { get; } = source;
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
