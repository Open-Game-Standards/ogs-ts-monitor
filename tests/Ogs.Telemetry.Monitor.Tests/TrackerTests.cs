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
        Environment = { Gravity = 9.5f },
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

    // ---- Several senders (implementation-udp.md § Senders and sessions) --

    static readonly System.Net.IPEndPoint SimA = new(System.Net.IPAddress.Parse("10.0.0.2"), 50001);
    static readonly System.Net.IPEndPoint SimB = new(System.Net.IPAddress.Parse("10.0.0.3"), 50002);

    [Fact]
    public void Senders_with_the_same_object_name_stay_apart()
    {
        var t = NewTracker();
        var a = Discovery(Car("player", true, new ChannelDescriptor { Id = 0, Name = "engine.rpm", Unit = "rpm", Type = ChannelType.Number }));
        a.GameName = "Sim A"; a.InstanceLabel = "Rig 1";
        var b = Discovery(Car("player", true, new ChannelDescriptor { Id = 0, Name = "vehicle.speed", Unit = "m/s", Type = ChannelType.Number }));
        b.GameName = "Sim B"; b.Host = "sim-pc";
        t.Ingest(Encode(FrameType.Discovery, a), SimA);
        t.Ingest(Encode(FrameType.Discovery, b), SimB);

        var ma = Motion(Pose("player")); ma.Objects![0].Channels = new List<ChannelValue> { new() { Id = 0, Number = 4000 } };
        var mb = Motion(Pose("player")); mb.Objects![0].Channels = new List<ChannelValue> { new() { Id = 0, Number = 30 } };
        t.Ingest(Encode(FrameType.Motion, ma), SimA);
        t.Ingest(Encode(FrameType.Motion, mb), SimB);

        var s = t.Snapshot();
        Assert.Equal(2, s.Sessions.Count);
        var sa = s.Sessions.Single(x => x.GameName == "Sim A");
        var sb = s.Sessions.Single(x => x.GameName == "Sim B");
        Assert.Equal("Rig 1", sa.InstanceLabel);
        Assert.Equal("sim-pc", sb.Host);
        Assert.Equal("10.0.0.2:50001", sa.Source);
        // each sender's id 0 resolves against its own declaration
        Assert.Equal("4000", sa.Objects.Single().Channels.Single().Display);
        Assert.Equal("30", sb.Objects.Single().Channels.Single().Display);
        Assert.Equal("number", sa.Objects.Single().Channels.Single().Type);
        Assert.Empty(s.Findings);
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_dropped_and_flagged()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car("car", true,
            new ChannelDescriptor { Id = 0, Name = "lights.headlights", Type = ChannelType.Boolean }))));
        var m = Motion(Pose("car")); m.Objects![0].Channels = new List<ChannelValue> { new() { Id = 0, Number = 1 } };
        t.Ingest(Encode(FrameType.Motion, m));
        var s = t.Snapshot();
        Assert.Null(s.Sessions.Single().Objects.Single().Channels.Single().Display);
        Assert.Contains("channel-type", Rules(s, Severity.Warning));
    }

    [Fact]
    public void A_sender_above_the_size_envelope_is_a_note()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car())), SimA);
        // 1,300-byte datagrams at 100 Hz for 5 s: 130 KB/s
        for (int i = 0; i <= 500; i++)
        {
            t.OnDatagram(FrameType.Motion, 1300, SimA);
            Advance(0.01);
        }
        var s = t.Snapshot();
        Assert.Contains("size-envelope", Rules(s, Severity.Info));
        Assert.DoesNotContain("size-envelope", Rules(s, Severity.Warning));
    }

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
        // the corpus discoveries are independent vectors that share one
        // session identity and generation 0, so as a "stream" they change
        // descriptors without a new generation — expected here, and only here
        var findings = s.Findings.Where(f => f.Rule != "generation-not-bumped").ToList();
        Assert.True(findings.Count == 0, string.Join("; ", findings.Select(f => $"{f.Rule}: {f.Message}")));
        Assert.Contains("generation-not-bumped", Rules(s, Severity.Warning));
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

    // ---- Datagram budget: paged discovery, motion parts, generations ----

    static string Findings(MonitorSnapshot s) =>
        string.Join("; ", s.Findings.Select(f => $"{f.Rule}: {f.Message}"));

    static ChannelDescriptor Ch(ushort id, string name, string unit = "m/s") =>
        new() { Id = id, Name = name, Unit = unit, Description = "a channel with a realistic description" };

    [Fact]
    public void Corpus_paged_discovery_and_motion_parts_merge_cleanly()
    {
        var t = NewTracker();
        foreach (var id in new[] { "discovery-paged-p0", "discovery-paged-p1", "motion-parts-p0", "motion-parts-p1" })
            Assert.True(t.Ingest(Corpus.Datagram(id)), id);

        var s = t.Snapshot();
        Assert.True(s.Findings.Count == 0, Findings(s));
        var session = s.Sessions.Single();
        Assert.Equal(3u, session.Generation);
        Assert.Equal(2, session.PageCount);
        Assert.Equal(2, session.PagesSeen);

        var car = session.Objects.Single();
        Assert.Equal("conformance car", car.TypeLabel);    // identity from page 0, not page 1's defaults
        Assert.True(car.Primary);
        var byName = car.Channels.ToDictionary(c => c.Name);
        Assert.Equal(3, byName.Count);                       // both pages' channels survive
        Assert.Equal("4100", byName["engine.rpm"].Display);
        Assert.Equal("Test Ring", byName["session.track"].Display);
        Assert.Equal("12.5", byName["vehicle.speed"].Display);
        Assert.Equal(4f, car.Pose!.Position.X);              // pose from part 0, not part 1's zeros
        Assert.Equal(1, s.Stats.MotionFramesPerSecond);      // one tick, two parts
    }

    [Fact]
    public void Pages_in_reverse_order_take_identity_from_the_lowest_page()
    {
        var t = NewTracker();
        t.Ingest(Corpus.Datagram("discovery-paged-p1"));
        Assert.DoesNotContain("no-primary", Rules(t.Snapshot()));  // incomplete: not judged yet
        t.Ingest(Corpus.Datagram("discovery-paged-p0"));
        var s = t.Snapshot();
        Assert.True(s.Findings.Count == 0, Findings(s));
        var car = s.Sessions.Single().Objects.Single();
        Assert.True(car.Primary);
        Assert.Equal("conformance car", car.TypeLabel);
    }

    [Fact]
    public void Values_for_pages_not_yet_seen_are_held_back()
    {
        var t = NewTracker();
        t.Ingest(Corpus.Datagram("discovery-paged-p0"));   // page 1 (channel 2) not here yet
        t.Ingest(Corpus.Datagram("motion-parts-p0"));
        t.Ingest(Corpus.Datagram("motion-parts-p1"));
        Assert.DoesNotContain("undeclared-channel", Rules(t.Snapshot()));
    }

    [Fact]
    public void Changed_descriptors_need_a_new_generation()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car(channels: new[] { Ch(0, "a.speed") }))));
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car(channels: new[] { Ch(0, "renamed.speed") }))));
        Assert.Contains("generation-not-bumped", Rules(t.Snapshot(), Severity.Warning));

        var t2 = NewTracker();
        t2.Ingest(Encode(FrameType.Discovery, Discovery(Car(channels: new[] { Ch(0, "a.speed"), Ch(1, "b.speed") }))));
        var next = Discovery(Car(channels: new[] { Ch(0, "renamed.speed") }));
        next.Generation = 1;
        t2.Ingest(Encode(FrameType.Discovery, next));
        var s = t2.Snapshot();
        Assert.True(s.Findings.Count == 0, Findings(s));
        Assert.Equal("renamed.speed", s.Sessions.Single().Objects.Single().Channels.Single().Name);
    }

    [Fact]
    public void Generation_jump_is_noted()
    {
        var t = NewTracker();
        t.Ingest(Encode(FrameType.Discovery, Discovery(Car())));
        var later = Discovery(Car(channels: new[] { Ch(0, "x.speed") }));
        later.Generation = 5;
        t.Ingest(Encode(FrameType.Discovery, later));
        Assert.Contains("generation-step", Rules(t.Snapshot(), Severity.Info));
    }

    [Fact]
    public void Oversized_datagrams_break_the_budget_paged_ones_do_not()
    {
        var channels = Enumerable.Range(0, 120).Select(i => Ch((ushort)i, $"sim.channel{i:D3}")).ToArray();
        var unpaged = NewTracker();
        unpaged.Ingest(Encode(FrameType.Discovery, Discovery(Car(channels: channels))));
        Assert.Contains("datagram-budget", Rules(unpaged.Snapshot(), Severity.Warning));

        var paged = NewTracker();
        var pages = DiscoveryPager.Paginate(() =>
        {
            var d = Discovery();
            d.Objects = new List<ObjectDescriptor>();
            return d;
        }, new[] { Car(channels: channels) }, generation: 0);
        Assert.True(pages.Count > 1);
        foreach (var page in pages) paged.Ingest(Encode(FrameType.Discovery, page));
        var s = paged.Snapshot();
        Assert.True(s.Findings.Count == 0, Findings(s));
        Assert.Equal(120, s.Sessions.Single().Objects.Single().Channels.Count);
    }

    [Fact]
    public void Paged_channels_drop_only_once_the_generation_is_complete()
    {
        var t = NewTracker();
        var first = Discovery(Car(channels: new[] { Ch(0, "a.speed"), Ch(1, "b.speed") }));
        t.Ingest(Encode(FrameType.Discovery, first));

        // generation 1 in two pages; channel 1 is no longer declared
        DiscoveryFrame Page(ushort page, ObjectDescriptor obj)
        {
            var d = Discovery(obj);
            d.Generation = 1; d.Page = page; d.PageCount = 2;
            return d;
        }
        t.Ingest(Encode(FrameType.Discovery, Page(0, Car(channels: new[] { Ch(0, "a.speed") }))));
        Assert.Equal(2, t.Snapshot().Sessions.Single().Objects.Single().Channels.Count); // not complete yet
        t.Ingest(Encode(FrameType.Discovery, Page(1, new ObjectDescriptor
        {
            Name = "car", Channels = new List<ChannelDescriptor> { Ch(2, "c.speed") },
        })));
        var s = t.Snapshot();
        Assert.True(s.Findings.Count == 0, Findings(s));
        Assert.Equal(new[] { "a.speed", "c.speed" },
            s.Sessions.Single().Objects.Single().Channels.Select(c => c.Name));
    }

    [Fact]
    public void Every_Negative_Vector_Ingests_Without_Throwing()
    {
        // each malformed datagram after a valid session, and the tracker still works afterwards
        var negatives = Corpus.Negative().ToList();
        Assert.True(negatives.Count >= 14);
        foreach (var (id, datagram) in negatives)
        {
            var t = NewTracker();
            t.Ingest(Corpus.Datagram("discovery-channels"));
            var ex = Record.Exception(() => { t.Ingest(datagram); t.Snapshot(); });
            Assert.True(ex is null, $"{id}: {ex}");
            Assert.True(t.Ingest(Corpus.Datagram("discovery-channels")), id);
        }
    }

    [Fact]
    public void Declared_Motion_Rate_Reaches_The_Session_View()
    {
        var t = NewTracker();
        var d = Discovery(Car());
        d.MotionRateHz = 50;
        t.Ingest(Encode(FrameType.Discovery, d));
        Assert.Equal(50f, t.Snapshot().Sessions.Single().DeclaredMotionRateHz);
    }
}
