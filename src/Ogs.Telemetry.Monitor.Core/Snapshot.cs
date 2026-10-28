using System.Numerics;

namespace Ogs.Telemetry.Monitor;

/// <summary>Everything the monitor shows, captured at one instant. Immutable;
/// produced by <see cref="StreamTracker.Snapshot"/> for the UI to poll.</summary>
public sealed record MonitorSnapshot(
    IReadOnlyList<SessionView> Sessions,
    IReadOnlyList<LogLine> Log,
    IReadOnlyList<EventView> Events,
    IReadOnlyList<Finding> Findings,
    StreamStats Stats);

/// <summary>One sender session: a discovery identity (game + session start).</summary>
public sealed record SessionView(
    string GameName,
    long SessionStartUnixUs,
    ushort SchemaVersion,
    string Medium,
    Vector3 Gravity,
    float AirDensity,
    float Temperature,
    float Pressure,
    double DiscoveryAgeSeconds,
    long DiscoveryCount,
    IReadOnlyList<ObjectView> Objects);

public sealed record ObjectView(
    string Key,
    string Name,
    string Type,
    string TypeLabel,
    string Location,
    bool Primary,
    int DrivePointCount,
    double MotionRateHz,
    double MotionAgeSeconds,
    double? Timestamp,
    PoseView? Pose,
    DynamicsView? Dynamics,
    Vector3 EffectiveGravity,
    bool LocalGravity,
    IReadOnlyList<ChannelView> Channels);

/// <summary>Pose in the OGS-TS world frame (left-handed: X right, Y up,
/// Z forward); angles in degrees (yaw right +, pitch nose-up +, roll
/// right-side-down +).</summary>
public sealed record PoseView(Vector3 Position, Quaternion Orientation, float Yaw, float Pitch, float Roll);

/// <summary>Body-frame motion cues. <see cref="Source"/> says where they
/// came from: <c>bodyDynamics</c> as sent, or derived from
/// <c>kinematics</c> (specific force only).</summary>
public sealed record DynamicsView(string Source, Vector3 SpecificForce, Vector3? AngularVelocity);

public sealed record ChannelView(
    ushort Id,
    string Name,
    string Unit,
    double? Min,
    double? Max,
    string Description,
    string? Display,
    double? Number,
    bool OutOfRange,
    double? AgeSeconds);

public sealed record EventView(
    uint Id,
    string Name,
    float Intensity,
    float Duration,
    Vector3? Direction,
    string Object,
    double Timestamp,
    int Copies,
    double AgeSeconds);

/// <summary>One motion sample of one object, for the time-history views.
/// <see cref="At"/> is monitor uptime in seconds (arrival time), so samples
/// from different senders share one axis.</summary>
public sealed record MotionSample(
    double At,
    Vector3 Position,
    Quaternion Orientation,
    float Yaw,
    float Pitch,
    float Roll,
    Vector3? SpecificForce,
    Vector3? AngularVelocity);

public enum Severity { Info, Warning }

/// <summary>A conformance finding, aggregated: the same rule and subject
/// count up rather than repeat.</summary>
public sealed record Finding(Severity Severity, string Rule, string Message, long Count, double AgeSeconds);

public enum LogKind { Discovery, Motion, Event, Finding }

public sealed record LogLine(long Seq, double At, LogKind Kind, string Text);

public sealed record StreamStats(
    long DiscoveryFrames,
    long MotionFrames,
    long EventFrames,
    double MotionFramesPerSecond,
    long Foreign,
    long DecodeErrors,
    double UptimeSeconds);
