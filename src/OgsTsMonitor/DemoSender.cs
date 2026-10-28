using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using Ogs.Telemetry;

namespace OgsTsMonitor;

/// <summary>
/// A built-in OGS-TS sender on the reference <see cref="TelemetryPublisher"/>,
/// so the monitor can be tried with no game running: a car on a winding,
/// rolling road with body dynamics, a handful of channels and a periodic
/// impact event. By default it sends through the loopback interface, so it
/// stays on this machine: a receiver driving a motion rig elsewhere on the
/// network never sees a demo car. <c>lan</c> sends on the default route
/// instead, like a real game.
/// </summary>
public sealed class DemoSender : IDisposable
{
    readonly TelemetrySender _sender;
    readonly TelemetryPublisher _pub;
    readonly Thread _thread;
    volatile bool _stop;

    public DemoSender(string group, int port, bool lan)
    {
        _sender = new TelemetrySender(group, port, localInterface: lan ? null : IPAddress.Loopback);
        Lan = lan;
        _pub = new TelemetryPublisher(_sender, "OGS-TS Monitor demo", motionHz: 60);
        _pub.DeclareObject("demo_car", "demo car");

        _pub.AddWheel("wheel_fl", new Vector3(-0.8f, 0f, 1.3f), 0.33f, driven: false, steered: true);
        _pub.AddWheel("wheel_fr", new Vector3(0.8f, 0f, 1.3f), 0.33f, driven: false, steered: true);
        _pub.AddWheel("wheel_rl", new Vector3(-0.8f, 0f, -1.3f), 0.33f, driven: true, steered: false);
        _pub.AddWheel("wheel_rr", new Vector3(0.8f, 0f, -1.3f), 0.33f, driven: true, steered: false);

        var speed = _pub.DeclareChannel("vehicle.speed", Unit.MetrePerSecond, "Speed over ground", 0, 90);
        var rpm = _pub.DeclareChannel("engine.rpm", Unit.Rpm, "Engine revolutions per minute", 0, 9000);
        var throttle = _pub.DeclareChannel("engine.throttle", Unit.Scalar, "Throttle pedal, 0..1", 0, 1);
        var headlights = _pub.DeclareBooleanChannel("lights.headlights", "Headlights on/off");

        _pub.BindChannelRaw(speed, () => Speed(_t));
        _pub.BindChannelRaw(rpm, () => 2200 + 5800 * (0.5 + 0.5 * Math.Sin(_t * 1.7)));
        _pub.BindChannelRaw(throttle, () => Math.Clamp(0.55 + 0.45 * Math.Cos(_t / 3.1), 0, 1));
        _pub.BindBooleanChannel(headlights, () => true);

        _pub.BindPose(() => _position, () => Orientation(_t));
        _pub.BindBodyDynamics(() => Dynamics(_t));

        _thread = new Thread(Run) { IsBackground = true, Name = "OGS-TS demo sender" };
        _thread.Start();
    }

    public bool Lan { get; }

    double _t;
    Vector3 _position;

    static double Speed(double t) => 32 + 10 * Math.Sin(t / 3.1);
    static double Yaw(double t) => 0.6 * Math.Sin(t / 4.0) + t * 0.05;          // rad
    static double Pitch(double t) => 0.07 * Math.Sin(t / 2.3);                  // rad, nose up +
    static double Roll(double t) => 0.13 * Math.Sin(t / 1.9);                   // rad, right side down +

    /// <summary>OGS-TS orientation from yaw, pitch and roll (left-handed:
    /// X right, Y up, Z forward): yaw about +Y, then pitch and roll about the
    /// body axes, signs per the standard's readout.</summary>
    static Quaternion Orientation(double t) =>
        Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Yaw(t))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)-Pitch(t))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)-Roll(t)));

    /// <summary>Accelerometer-style specific force and body rates: cornering
    /// pulls sideways, speed changes push fore/aft, the road adds a little
    /// heave on top of the 1 g gravity reaction.</summary>
    static (Vector3, Vector3) Dynamics(double t)
    {
        const double g = 9.80665;
        double v = Speed(t);
        double yawRate = 0.6 / 4.0 * Math.Cos(t / 4.0) + 0.05;
        double lateral = v * yawRate;                                 // m/s², centripetal
        double longitudinal = 10.0 / 3.1 * Math.Cos(t / 3.1);         // dv/dt
        double heave = g + 0.8 * Math.Sin(t * 5.3);
        var specificForce = new Vector3((float)lateral, (float)heave, (float)longitudinal);
        var rates = new Vector3(
            (float)(0.07 / 2.3 * Math.Cos(t / 2.3)),                  // pitch rate, nose-up +
            (float)yawRate,                                           // yaw rate, right +
            (float)(0.13 / 1.9 * Math.Cos(t / 1.9)));                 // roll rate, right-down +
        return (specificForce, rates);
    }

    void Run()
    {
        // Windows sleeps in ~15.6 ms steps by default, which alternately misses
        // and hits the publisher's 16.7 ms motion gate (≈32 Hz). Ask for 1 ms.
        bool fineTimer = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
        try { Loop(); }
        finally { if (fineTimer) TimeEndPeriod(1); }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] static extern uint TimeBeginPeriod(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] static extern uint TimeEndPeriod(uint ms);

    void Loop()
    {
        var clock = Stopwatch.StartNew();
        double lastEvent = 0, last = 0;
        var heading = Vector3.Zero;
        while (!_stop)
        {
            double t = clock.Elapsed.TotalSeconds;
            double dt = t - last;
            last = t;
            _t = t;
            var forward = Vector3.Transform(Vector3.UnitZ, Orientation(t));
            heading += forward * (float)(Speed(t) * dt);
            _position = heading;

            if (t - lastEvent >= 5.0)
            {
                lastEvent = t;
                float side = Math.Sin(t) > 0 ? 1f : -1f;
                _pub.FireEvent("body.impact", 0.35f + 0.5f * (float)Math.Abs(Math.Sin(t * 0.7)), 0f,
                    new Vector3(side, 0f, 0.3f));
            }
            _pub.Tick(t);
            Thread.Sleep(2);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(1));
        _pub.Dispose();
        _sender.Dispose();
    }
}
