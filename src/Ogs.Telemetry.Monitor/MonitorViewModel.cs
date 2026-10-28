using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Ogs.Telemetry.Monitor.Controls;

namespace Ogs.Telemetry.Monitor;

/// <summary>Attitude drawing: body axes from behind (heading removed), body
/// axes against fixed world axes, or an aircraft horizon.</summary>
public enum AttitudeMode { Chase, World, Horizon }

/// <summary>
/// Presents a <see cref="StreamTracker"/> for <c>MonitorView</c>. The host
/// owns the tracker (and whatever feeds it); the view calls
/// <see cref="Refresh"/> on a UI timer. Rows update in place and lists are
/// only rebuilt when their membership changes, so a 30 Hz refresh is cheap.
/// </summary>
public sealed class MonitorViewModel : Observable
{
    const double StandardGravity = 9.80665;
    readonly StreamTracker _tracker;
    long _lastLogSeq;
    string _objectsSignature = "", _eventsSignature = "", _findingsSignature = "", _channelsSignature = "";

    public MonitorViewModel(StreamTracker tracker) => _tracker = tracker;

    public StreamTracker Tracker => _tracker;

    public ObservableCollection<ObjectItem> Objects { get; } = new();
    public ObservableCollection<ChannelRow> Channels { get; } = new();
    public ObservableCollection<LogRow> Log { get; } = new();
    public ObservableCollection<EventRow> Events { get; } = new();
    public ObservableCollection<FindingRow> Findings { get; } = new();

    /// <summary>Raised after <see cref="Refresh"/> appends log lines.</summary>
    public event Action? LogAppended;

    ObjectItem? _selected;
    /// <summary>The object shown in detail. Follows the newest session's
    /// primary until the user picks one.</summary>
    public ObjectItem? SelectedObject
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (!_selecting) _followPrimary = value is null;
            _channelsSignature = "";
        }
    }
    bool _followPrimary = true, _selecting;

    // ---- Selected object ---------------------------------------------------

    string _game = "Waiting for a sender…";
    public string GameName { get => _game; private set => Set(ref _game, value); }

    string _sessionInfo = "Listening for OGS-TS discovery frames on the local network.";
    public string SessionInfo { get => _sessionInfo; private set => Set(ref _sessionInfo, value); }

    bool _hasSession;
    public bool HasSession { get => _hasSession; private set => Set(ref _hasSession, value); }

    string _objectTitle = "";
    public string ObjectTitle { get => _objectTitle; private set => Set(ref _objectTitle, value); }

    string _objectInfo = "";
    public string ObjectInfo { get => _objectInfo; private set => Set(ref _objectInfo, value); }

    bool _poseLive;
    public bool PoseLive { get => _poseLive; private set => Set(ref _poseLive, value); }

    double _yaw, _pitch, _roll;
    public double Yaw { get => _yaw; private set => Set(ref _yaw, value); }
    public double Pitch { get => _pitch; private set => Set(ref _pitch, value); }
    public double Roll { get => _roll; private set => Set(ref _roll, value); }

    string _yawText = "—", _pitchText = "—", _rollText = "—", _positionText = "—", _rateText = "—";
    public string YawText { get => _yawText; private set => Set(ref _yawText, value); }
    public string PitchText { get => _pitchText; private set => Set(ref _pitchText, value); }
    public string RollText { get => _rollText; private set => Set(ref _rollText, value); }
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }
    public string RateText { get => _rateText; private set => Set(ref _rateText, value); }

    string _dynamicsSource = "no dynamics";
    public string DynamicsSource { get => _dynamicsSource; private set => Set(ref _dynamicsSource, value); }

    string _angularText = "—", _gravityText = "—";
    public string AngularText { get => _angularText; private set => Set(ref _angularText, value); }
    public string GravityText { get => _gravityText; private set => Set(ref _gravityText, value); }

    // ---- Motion views --------------------------------------------------------

    Quaternion _orientation = Quaternion.Identity;
    public Quaternion Orientation { get => _orientation; private set => Set(ref _orientation, value); }

    IReadOnlyList<Vector3> _track = Array.Empty<Vector3>();
    public IReadOnlyList<Vector3> Track { get => _track; private set => Set(ref _track, value); }

    IReadOnlyList<StripLane> _lanes = Array.Empty<StripLane>();
    public IReadOnlyList<StripLane> Lanes { get => _lanes; private set => Set(ref _lanes, value); }

    double _now;
    /// <summary>Right edge of the strip chart, on the history's time axis.</summary>
    public double Now { get => _now; private set => Set(ref _now, value); }

    public double HistorySeconds => StreamTracker.HistoryWindow.TotalSeconds;

    AttitudeMode _mode = AttitudeMode.Chase;
    /// <summary>How attitude is drawn. Defaults to the horizon for aircraft
    /// and chase for everything else when an object is selected.</summary>
    public AttitudeMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(IsChaseMode)); Raise(nameof(IsWorldMode)); Raise(nameof(IsHorizonMode));
            Raise(nameof(UseAxes)); Raise(nameof(UseHorizon)); Raise(nameof(Chase));
        }
    }

    // One per mode button: checking one selects it; unchecking the selected
    // one is refused (the button re-reads true), so exactly one stays on.
    public bool IsChaseMode { get => Mode == AttitudeMode.Chase; set => Select(AttitudeMode.Chase, value, nameof(IsChaseMode)); }
    public bool IsWorldMode { get => Mode == AttitudeMode.World; set => Select(AttitudeMode.World, value, nameof(IsWorldMode)); }
    public bool IsHorizonMode { get => Mode == AttitudeMode.Horizon; set => Select(AttitudeMode.Horizon, value, nameof(IsHorizonMode)); }

    void Select(AttitudeMode mode, bool on, string property)
    {
        if (on) Mode = mode;
        else if (Mode == mode) Raise(property);
    }

    public bool UseHorizon => Mode == AttitudeMode.Horizon;
    public bool UseAxes => Mode != AttitudeMode.Horizon;
    /// <summary>Body axes seen from behind with heading removed, rather than world-fixed.</summary>
    public bool Chase => Mode == AttitudeMode.Chase;

    bool _nearVertical;
    /// <summary>Nose within 5° of straight up or down: yaw and roll angles are ill-defined.</summary>
    public bool NearVertical { get => _nearVertical; private set => Set(ref _nearVertical, value); }

    string? _viewKey;

    // ---- Stream ------------------------------------------------------------

    string _stats = "";
    public string StatsText { get => _stats; private set => Set(ref _stats, value); }

    int _warnings, _notes;
    public int WarningCount { get => _warnings; private set { if (Set(ref _warnings, value)) FindingsChanged(); } }
    public int NoteCount { get => _notes; private set { if (Set(ref _notes, value)) FindingsChanged(); } }
    public bool NoFindings => WarningCount + NoteCount == 0;
    void FindingsChanged() { Raise(nameof(FindingsHeader)); Raise(nameof(NoFindings)); }
    public string FindingsHeader => WarningCount + NoteCount == 0
        ? "Conformance ✓"
        : $"Conformance ({WarningCount} warning{(WarningCount == 1 ? "" : "s")}, {NoteCount} note{(NoteCount == 1 ? "" : "s")})";

    string _eventsHeader = "Events";
    public string EventsHeader { get => _eventsHeader; private set => Set(ref _eventsHeader, value); }

    // ---- Refresh -----------------------------------------------------------

    public void Clear()
    {
        _tracker.Clear();
        Log.Clear();
        _lastLogSeq = 0;
        _followPrimary = true;
        Refresh();
    }

    public void Refresh()
    {
        var s = _tracker.Snapshot();
        RefreshObjects(s);
        var (session, obj) = Find(s, _selected?.Key);
        RefreshSession(session);
        RefreshObject(obj);
        RefreshLog(s);
        RefreshEvents(s);
        RefreshFindings(s);
        var st = s.Stats;
        StatsText = Inv($"{st.MotionFramesPerSecond:0} motion/s · {st.DiscoveryFrames} discovery · {st.MotionFrames} motion · {st.EventFrames} event")
            + (st.Foreign > 0 ? Inv($" · {st.Foreign} foreign") : "")
            + (st.DecodeErrors > 0 ? Inv($" · {st.DecodeErrors} decode errors") : "");
    }

    void RefreshObjects(MonitorSnapshot s)
    {
        var all = s.Sessions.SelectMany(sv => sv.Objects.Select(o => (sv, o))).ToList();
        string sig = string.Join("\n", all.Select(x => x.o.Key));
        if (sig != _objectsSignature)
        {
            _objectsSignature = sig;
            string? keep = _selected?.Key;
            _selecting = true;
            Objects.Clear();
            foreach (var (_, o) in all) Objects.Add(new ObjectItem(o.Key, o.Name, o.Primary));
            SelectedObject = Objects.FirstOrDefault(i => i.Key == keep);
            _selecting = false;
        }
        foreach (var (sv, o) in all)
        {
            var item = Objects.FirstOrDefault(i => i.Key == o.Key);
            if (item is null) continue;
            item.IsLive = o.MotionRateHz > 0;
            item.Detail = $"{sv.GameName} · {Label(o)}" + (o.MotionRateHz > 0 ? Inv($" · {o.MotionRateHz:0} Hz") : "");
        }
        if (_followPrimary || _selected is null)
        {
            var newest = s.Sessions.FirstOrDefault();
            var primary = newest?.Objects.FirstOrDefault(o => o.Primary) ?? newest?.Objects.FirstOrDefault();
            var target = primary is null ? null : Objects.FirstOrDefault(i => i.Key == primary.Key);
            if (target != _selected)
            {
                _selecting = true;
                SelectedObject = target;
                _selecting = false;
            }
        }
    }

    static (SessionView?, ObjectView?) Find(MonitorSnapshot s, string? key)
    {
        if (key is null) return (s.Sessions.FirstOrDefault(), null);
        foreach (var sv in s.Sessions)
            foreach (var o in sv.Objects)
                if (o.Key == key) return (sv, o);
        return (s.Sessions.FirstOrDefault(), null);
    }

    void RefreshSession(SessionView? sv)
    {
        HasSession = sv is not null;
        if (sv is null)
        {
            GameName = "Waiting for a sender…";
            SessionInfo = "Listening for OGS-TS discovery frames on the local network.";
            return;
        }
        GameName = sv.GameName.Length == 0 ? "(unnamed game)" : sv.GameName;
        var started = DateTimeOffset.FromUnixTimeMilliseconds(sv.SessionStartUnixUs / 1000).ToLocalTime();
        SessionInfo = string.Create(CultureInfo.InvariantCulture,
            $"schema {sv.SchemaVersion} · {sv.Medium} · session from {started:HH:mm:ss} · " +
            $"discovery {sv.DiscoveryAgeSeconds:0.0} s ago ({sv.DiscoveryCount}) · " +
            $"air {sv.AirDensity:0.###} kg/m³ · {sv.Temperature:0.#} °C · {sv.Pressure:0} Pa");
    }

    void RefreshObject(ObjectView? o)
    {
        if (o is null)
        {
            ObjectTitle = "";
            ObjectInfo = "";
            PoseLive = false;
            YawText = PitchText = RollText = PositionText = RateText = "—";
            DynamicsSource = "no dynamics";
            AngularText = GravityText = "—";
            Track = Array.Empty<Vector3>();
            Lanes = Array.Empty<StripLane>();
            NearVertical = false;
            _viewKey = null;
            if (Channels.Count > 0) Channels.Clear();
            _channelsSignature = "";
            return;
        }

        ObjectTitle = o.Name + (o.Primary ? "  ★ primary" : "");
        ObjectInfo = string.Join(" · ", new[]
        {
            Label(o),
            o.Location,
            o.DrivePointCount > 0 ? $"{o.DrivePointCount} drive points" : "",
            o.Timestamp is double t ? string.Create(CultureInfo.InvariantCulture, $"t {t:0.00} s") : "",
        }.Where(x => x.Length > 0));
        RateText = o.MotionRateHz > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{o.MotionRateHz:0} Hz")
            : double.IsFinite(o.MotionAgeSeconds)
                ? string.Create(CultureInfo.InvariantCulture, $"stalled {o.MotionAgeSeconds:0} s")
                : "no motion yet";

        PoseLive = o.Pose is not null && o.MotionAgeSeconds < 2;
        if (o.Pose is { } p)
        {
            Yaw = p.Yaw; Pitch = p.Pitch; Roll = p.Roll;
            YawText = Deg(p.Yaw); PitchText = Deg(p.Pitch); RollText = Deg(p.Roll);
            PositionText = Vec(p.Position, "0.00") + " m";
        }
        else
        {
            YawText = PitchText = RollText = PositionText = "—";
        }

        GravityText = Vec(o.EffectiveGravity, "0.##") + " m/s²" + (o.LocalGravity ? " (local)" : "");
        if (o.Key != _viewKey)
        {
            _viewKey = o.Key;
            Mode = o.Type == "aircraft" ? AttitudeMode.Horizon : AttitudeMode.Chase;
        }
        RefreshMotionViews(o);
        if (o.Dynamics is { } d)
        {
            DynamicsSource = d.Source == "bodyDynamics" ? "bodyDynamics (as sent)" : "specific force from kinematics";
            AngularText = d.AngularVelocity is { } w
                ? string.Create(CultureInfo.InvariantCulture,
                    $"pitch {w.X * 180 / Math.PI:+0.0;-0.0} · yaw {w.Y * 180 / Math.PI:+0.0;-0.0} · roll {w.Z * 180 / Math.PI:+0.0;-0.0} °/s")
                : "—";
        }
        else
        {
            DynamicsSource = "no dynamics (pose only)";
            AngularText = "—";
        }

        string sig = o.Key + "\n" + string.Join(",", o.Channels.Select(c => $"{c.Id}:{c.Name}:{c.Unit}:{c.Min}:{c.Max}"));
        if (sig != _channelsSignature)
        {
            _channelsSignature = sig;
            Channels.Clear();
            foreach (var c in o.Channels) Channels.Add(new ChannelRow(c));
        }
        else
        {
            for (int i = 0; i < o.Channels.Count; i++) Channels[i].Update(o.Channels[i]);
        }
    }

    void RefreshMotionViews(ObjectView o)
    {
        var history = _tracker.History(o.Key);
        Now = _tracker.Now();
        if (o.Pose is { } p)
        {
            Orientation = p.Orientation;
            NearVertical = MathF.Abs(p.Pitch) > 85f;
        }
        int n = history.Count;
        var track = new Vector3[n];
        var t = new double[n];
        double[] roll = new double[n], pitch = new double[n], yawRate = new double[n],
                 surge = new double[n], sway = new double[n], heave = new double[n], height = new double[n];
        for (int i = 0; i < n; i++)
        {
            var h = history[i];
            track[i] = h.Position;
            t[i] = h.At;
            roll[i] = h.Roll;
            pitch[i] = h.Pitch;
            height[i] = h.Position.Y;
            yawRate[i] = h.AngularVelocity is { } w ? w.Y * 180 / Math.PI : double.NaN;
            if (h.SpecificForce is { } f)
            {
                sway[i] = f.X / StandardGravity;
                heave[i] = f.Y / StandardGravity;
                surge[i] = f.Z / StandardGravity;
            }
            else surge[i] = sway[i] = heave[i] = double.NaN;
        }
        Track = track;
        const string Deg1 = "+0.0;-0.0;0.0", G2 = "+0.00;-0.00;0.00";
        Lanes = new[]
        {
            new StripLane("roll", "°", t, roll, 2, Last(roll, Deg1, "°")),
            new StripLane("pitch", "°", t, pitch, 2, Last(pitch, Deg1, "°")),
            new StripLane("yaw rate", "°/s", t, yawRate, 5, Last(yawRate, Deg1, "°/s")),
            new StripLane("surge  z", "g", t, surge, 0.1, Last(surge, G2, "g")),
            new StripLane("sway   x", "g", t, sway, 0.1, Last(sway, G2, "g")),
            new StripLane("heave  y", "g", t, heave, 0.1, Last(heave, G2, "g")),
            new StripLane("height", "m", t, height, 1, Last(height, "0.00", "m")),
        };
    }

    static string Last(double[] v, string format, string unit)
    {
        for (int i = v.Length - 1; i >= 0; i--)
            if (!double.IsNaN(v[i])) return v[i].ToString(format, CultureInfo.InvariantCulture) + " " + unit;
        return "—";
    }

    void RefreshLog(MonitorSnapshot s)
    {
        bool appended = false;
        foreach (var line in s.Log)
        {
            if (line.Seq <= _lastLogSeq) continue;
            Log.Add(new LogRow(line));
            _lastLogSeq = line.Seq;
            appended = true;
        }
        while (Log.Count > StreamTracker.MaxLogLines) Log.RemoveAt(0);
        if (appended) LogAppended?.Invoke();
    }

    void RefreshEvents(MonitorSnapshot s)
    {
        string sig = s.Events.Count == 0 ? "" : $"{s.Events.Count}:{s.Events[0].Id}:{s.Events.Sum(e => e.Copies)}";
        if (sig == _eventsSignature) return;
        _eventsSignature = sig;
        Events.Clear();
        foreach (var e in s.Events) Events.Add(new EventRow(e));
        EventsHeader = s.Events.Count == 0 ? "Events" : $"Events ({s.Events.Count})";
    }

    void RefreshFindings(MonitorSnapshot s)
    {
        WarningCount = s.Findings.Count(f => f.Severity == Severity.Warning);
        NoteCount = s.Findings.Count - WarningCount;
        // ages tick every second; rebuild at most that often
        string sig = string.Join("|", s.Findings.Select(f => $"{f.Rule}:{f.Message}:{f.Count}:{(int)f.AgeSeconds}"));
        if (sig == _findingsSignature) return;
        _findingsSignature = sig;
        Findings.Clear();
        foreach (var f in s.Findings) Findings.Add(new FindingRow(f));
    }

    static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    static string Label(ObjectView o) => o.TypeLabel.Length > 0 ? $"{o.Type} · {o.TypeLabel}" : o.Type;
    static string Deg(float v) => (v + 0f).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "°";
    static string Vec(Vector3 v, string f) =>
        $"({v.X.ToString(f, CultureInfo.InvariantCulture)}, {v.Y.ToString(f, CultureInfo.InvariantCulture)}, {v.Z.ToString(f, CultureInfo.InvariantCulture)})";
}
