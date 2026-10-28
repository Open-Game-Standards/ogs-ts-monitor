using Ogs.Telemetry;
using Ogs.Telemetry.Monitor;
using Ogs.Telemetry.Schema;
using Ogs.Telemetry.Wire;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Ogs.Telemetry.Monitor.Tests;

public class TrackerTests
{
    // A manual clock: 1 tick = 1 µs.
    long _now;
    StreamTracker NewTracker() => new(() => _now, 1_000_000);
    void Advance(double seconds) => _now += (long)(seconds * 1_000_000);

    static byte[] Encode(FrameType type, WireStruct frame) =>
        CanonicalEncoder.EncodeDatagram(type, frame);

    static DiscoveryFrame Discovery(params ObjectDescriptor[] objects) => new()
    {
        SchemaVersion = 1,
        GameName = "test",
        SessionStartUnixUs = 1,
        Environment = { Gravity = 9.5f, AirDensity = 1.25f },
        Objects = new List<ObjectDescriptor>(objects),
    };

    static ObjectDescriptor Car(string name = "car", bool primary = true, params ChannelDescriptor[] channels) => new()
    {
        Name = name,
        Type = ObjectType.Vehicle,
        Primary = primary,
        Channels = new List<ChannelDescriptor>(channels),
    };

    static MotionObject Pose(string name, float w = 1, float y = 0) => new()
    {
        Name = name,
        Orientation = { W = w, Y = y },
    };

    static MotionFrame Motion(params MotionObject[] objects) =>
        new() { Timestamp = 1, Objects = new List<MotionObject>(objects) };

    static EventFrame Events(params Event[] events) =>
        new() { Timestamp = 1, Events = new List<Event>(events) };

    static IEnumerable<string> Rules(MonitorSnapshot s, Severity? severity = null) =>
        s.Findings.Where(f => severity is null || f.Severity == severity).Select(f => f.Rule);

    // ---- The standard's corpus -------------------------------------------

    [Fact]
    public void Corpus_pairs_decode_cleanly()
    {
        var t = NewTracker();
        Assert.True(t.Ingest(Corpus.Datagram("discovery-minimal")));
        foreach (var id in new[] { "motion-pose", "motion-local-gravity", "motion-local-gravity-zero" })
            Assert.True(t.Ingest(Corpus.Datagram(id)), id);
        Assert.True(t.Ingest(Corpus.Datagram("discovery-channels")));
        Assert.True(t.Ingest(Corpus.Datagram("motion-channels")));
        Assert.True(t.Ingest(Corpus.Datagram("discovery-gravity-vector")));
        Assert.True(t.Ingest(Corpus.Datagram("event-impact")));

        var s = t.Snapshot();
        Assert.True(s.Findings.Count == 0, string.Join("; ", s.Findings.Select(f => $"{f.Rule}: {f.Message}")));
        Assert.Equal(0, s.Stats.DecodeErrors);
        Assert.Single(s.Sessions); // all corpus discoveries share one identity
        Assert.Single(s.Events);
    }

    [Fact]
    public void Corpus_bodydynamics_orientation_is_not_unit_length()
    {
        // motion-bodydynamics carries orientation (y 0.5, w 0.875): float32-exact,
        // but |q| = 1.0078, so the reference receiver's 1e-3 check flags it too.
        // Pinned here so a corpus fix shows up as this test failing.
        var t = NewTracker();
        t.Ingest(Corpus.Datagram("discovery-minimal"));
        t.Ingest(Corpus.Datagram("motion-bodydynamics"));
        var f = Assert.Single(t.Snapshot().Findings);
        Assert.Equal("quaternion-norm", f.Rule);
    }

    [Fact]
    public void Corpus_channels_show_declared_values()
    {
        var t = NewTracker();
        t.Ingest(Corpus.Datagram("discovery-channels"));
        t.Ingest(Corpus.Datagram("motion-channels"));

        var car = t.Snapshot().Sessions.Single().Objects.Single(o => o.Name == "demo_car");
        Assert.True(car.Primary);
        Assert.Equal("conformance car", car.TypeLabel);
        var byName = car.Channels.ToDictionary(c => c.Name);
        Assert.Equal("3250.5", byName["engine.rpm"].Display);
        Assert.Equal(0, byName["engine.rpm"].Min);
        Assert.Equal(9000, byName["engine.rpm"].Max);
        Assert.Equal("8", byName["vehicle.speed"].Display);
        Assert.Equal("true", byName["lights.headlights"].Display);
        // identity rotated 180° about Y: facing −Z
        Assert.Equal(180f, MathF.Abs(car.Pose!.Yaw), 3);
    }

    [Fact]
    public void Corpus_pose_and_local_gravity()
    {
        var t = NewTracker();
        t.Ingest(Corpus.Datagram("discovery-minimal"));
        t.Ingest(Corpus.Datagram("motion-pose"));
        var obj = t.Snapshot().Sessions.Single().Objects.Single();
        Assert.Equal(new Vector3(1.5f, 0.25f, -3.75f), obj.Pose!.Position);
        Assert.Equal((0f, 0f, 0f), (obj.Pose.Yaw, obj.Pose.Pitch, obj.Pose.Roll));
        Assert.Equal(new Vector3(0, -9.5f, 0), obj.EffectiveGravity);
        Assert.False(obj.LocalGravity);

        t.Ingest(Corpus.Datagram("motion-local-gravity"));
        obj = t.Snapshot().Sessions.Single().Objects.Single();
        Assert.True(obj.LocalGravity);
        Assert.Equal(new Vector3(-2f, 0, 0), obj.EffectiveGravity);
        Assert.Equal("bodyDynamics", obj.Dynamics!.Source);
        Assert.Equal(new Vector3(2f, 0, 0), obj.Dynamics.SpecificForce);
    }

    [Fact]
    public void Corpus_event_replays_are_deduplicated()
    {
        var t = NewTracker();
        t.Ingest(Corpus.Datagram("event-impact"));
        t.Ingest(Corpus.Datagram("event-impact"));
        var s = t.Snapshot();
        var ev = Assert.Single(s.Events);
        Assert.Equal(2, ev.Copies);
        Assert.Equal(2, s.Stats.EventFrames);
        Assert.Single(s.Log, l => l.Kind == LogKind.Event);
    }

    // ---- Findings ---------------------------------------------------------

    [Fact]
    public void Non_unit_quaternion_is_flagged_once_and_counted()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car())));
        t.Ingest(Encode(FrameType.Motion, Motion(Pose("car", w: 2))));
        t.Ingest(Encode(FrameType.Motion, Motion(Pose("car", w: 2))));
        var s = t.Snapshot();
        var f = Assert.Single(s.Findings);
        Assert.Equal("quaternion-norm", f.Rule);
        Assert.Equal(2, f.Count);
        Assert.Single(s.Log, l => l.Kind == LogKind.Finding);
    }

    [Fact]
    public void Undeclared_object_and_channel_are_flagged()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car())));
        var withChannel = Pose("car");
        withChannel.Channels = new List<ChannelValue> { new() { Id = 9, Number = 1 } };
        t.Ingest(Encode(FrameType.Motion, Motion(withChannel)));
        t.Ingest(Encode(FrameType.Motion, Motion(Pose("ghost"))));
        Assert.Equal(new[] { "undeclared-channel", "undeclared-object" },
            Rules(t.Snapshot()).OrderBy(r => r));
    }

    [Fact]
    public void Primary_must_come_first_in_motion()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car("lead"), Car("wing", primary: false))));
        t.Ingest(Encode(FrameType.Motion, Motion(Pose("lead"), Pose("wing"))));
        Assert.Empty(t.Snapshot().Findings);
        t.Ingest(Encode(FrameType.Motion, Motion(Pose("wing"), Pose("lead"))));
        Assert.Equal(new[] { "primary-first" }, Rules(t.Snapshot()));
    }

    [Fact]
    public void Discovery_checks()
    {
        var t = NewTracker();
        var d = Discovery(Car(primary: false));
        d.SchemaVersion = 2;
        var g = d.Environment.InitGravityVector(); // |(0,−5,0)| = 5, but gravity says 9.5
        g.Y = -5;
        t.Ingest(Encode(FrameType.Discovery, d));
        Assert.Equal(new[] { "gravity-magnitude", "no-primary", "schema-version" },
            Rules(t.Snapshot()).OrderBy(r => r));
    }

    [Fact]
    public void Duplicate_channel_ids_are_flagged()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car("car", true,
            new ChannelDescriptor { Id = 1, Name = "a" },
            new ChannelDescriptor { Id = 1, Name = "b" }))));
        Assert.Equal(new[] { "duplicate-channel-id" }, Rules(t.Snapshot()));
    }

    [Fact]
    public void Event_checks()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Event, Events(new Event { Id = 5, Name = "a", Intensity = 1.5f })));
        t.Ingest(Encode(FrameType.Event, Events(new Event { Id = 3, Name = "b", Intensity = 0.5f })));
        Assert.Equal(new[] { "event-id-order", "event-intensity" }, Rules(t.Snapshot()).OrderBy(r => r));
    }

    [Fact]
    public void Out_of_range_and_motion_before_discovery_are_notes()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Motion, Motion(Pose("car"))));
        var speed = new ChannelDescriptor { Id = 0, Name = "vehicle.speed", Unit = "m/s" };
        speed.InitRange().Max = 30;
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car("car", true, speed))));
        var m = Pose("car");
        m.Channels = new List<ChannelValue> { new() { Id = 0, Number = 31 } };
        t.Ingest(Encode(FrameType.Motion, Motion(m)));

        var s = t.Snapshot();
        Assert.Empty(Rules(s, Severity.Warning));
        Assert.Equal(new[] { "motion-before-discovery", "out-of-range" }, Rules(s, Severity.Info).OrderBy(r => r));
        Assert.True(s.Sessions.Single().Objects.Single().Channels.Single().OutOfRange);
    }

    // ---- Dynamics, rates, stats --------------------------------------------

    [Fact]
    public void Kinematics_at_rest_reads_one_g_up()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car())));
        var m = Pose("car");
        m.InitKinematics(); // zero velocity and acceleration
        t.Ingest(Encode(FrameType.Motion, Motion(m)));
        var dyn = t.Snapshot().Sessions.Single().Objects.Single().Dynamics!;
        Assert.Equal("kinematics", dyn.Source);
        Assert.Equal(new Vector3(0, 9.5f, 0), dyn.SpecificForce);
        Assert.Null(dyn.AngularVelocity);
    }

    [Fact]
    public void Motion_rate_is_measured_per_object()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car())));
        var frame = Encode(FrameType.Motion, Motion(Pose("car")));
        for (int i = 0; i < 61; i++)
        {
            t.Ingest(frame);
            Advance(1.0 / 60);
        }
        var s = t.Snapshot();
        Assert.Equal(60, s.Sessions.Single().Objects.Single().MotionRateHz, 0);
        Assert.InRange(s.Stats.MotionFramesPerSecond, 59, 61);
        // throttled: one motion line per 500 ms, not one per frame
        Assert.InRange(s.Log.Count(l => l.Kind == LogKind.Motion), 2, 3);

        Advance(3);
        Assert.Equal(0, t.Snapshot().Sessions.Single().Objects.Single().MotionRateHz);
    }

    [Fact]
    public void History_keeps_the_last_window_per_object()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car())));
        for (int i = 0; i < 40; i++)
        {
            var m = Pose("car");
            m.Position.X = i;
            t.Ingest(Encode(FrameType.Motion, Motion(m)));
            Advance(1);
        }
        var key = t.Snapshot().Sessions.Single().Objects.Single().Key;
        var history = t.History(key);
        // 1 sample/s for 40 s; the 30 s window keeps the newest 31 (inclusive)
        Assert.InRange(history.Count, 30, 31);
        Assert.Equal(39f, history[^1].Position.X);
        Assert.True(history[0].At >= history[^1].At - StreamTracker.HistoryWindow.TotalSeconds);
        Assert.Empty(t.History("nope"));
    }

    [Fact]
    public void Foreign_packets_are_counted_not_decoded()
    {
        var t = NewTracker();
        Assert.False(t.Ingest(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        Assert.Equal(1, t.Snapshot().Stats.Foreign);
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var t = NewTracker();
        t.Ingest(Corpus.Datagram("discovery-minimal"));
        t.Ingest(Encode(FrameType.Motion, Motion(Pose("car", w: 2))));
        t.Clear();
        var s = t.Snapshot();
        Assert.Empty(s.Sessions);
        Assert.Empty(s.Findings);
        Assert.Empty(s.Log);
        Assert.Equal(0, s.Stats.DiscoveryFrames);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(30f, 0f, 0f)]
    [InlineData(0f, 20f, 0f)]
    [InlineData(0f, 0f, 15f)]
    [InlineData(-45f, 10f, -25f)]
    public void Euler_round_trips_ZYX_in_the_OGS_frame(float yaw, float pitch, float roll)
    {
        // OGS-TS: yaw right +, pitch nose-up +, roll right-side-down +. Build
        // the orientation as yaw, then pitch, then roll about the body axes,
        // check its forward vector against the intended heading, then check
        // the readout decomposes it back to the same three angles.
        const float d2r = MathF.PI / 180f;
        var forward = new Vector3(
            MathF.Cos(pitch * d2r) * MathF.Sin(yaw * d2r),
            MathF.Sin(pitch * d2r),
            MathF.Cos(pitch * d2r) * MathF.Cos(yaw * d2r));
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * d2r);
        q = Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch * d2r));
        q = Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -roll * d2r));
        var f = Vector3.Transform(Vector3.UnitZ, q);
        Assert.Equal(forward.X, f.X, 3);
        Assert.Equal(forward.Y, f.Y, 3);
        Assert.Equal(forward.Z, f.Z, 3);

        var (y, p, r) = Euler.FromQuaternion(q);
        Assert.Equal(yaw, y, 2);
        Assert.Equal(pitch, p, 2);
        Assert.Equal(roll, r, 2);
    }
}
