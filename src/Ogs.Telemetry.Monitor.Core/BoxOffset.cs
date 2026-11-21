using System.Numerics;

namespace Ogs.Telemetry.Monitor;

/// <summary>
/// Where the world-view box sits for an object's linear acceleration, in the view's units
/// (x right, y up, z forward; the box is 2.0 long): the telemetry's specific force carried into
/// the world frame, with gravity taken off, so an object at rest (level or tilted) sits still.
/// </summary>
public static class BoxOffset
{
    /// <summary>Box travel per 1 g of acceleration (view units).</summary>
    public const float PerG = 0.25f;

    /// <summary>The box never strays further than this on any axis, so it stays over the ground grid.</summary>
    public const float Max = 0.5f;

    const float StandardG = 9.80665f;

    /// <param name="specificForce">Body frame, m/s², accelerometer-style (x right, y up, z forward): f = a − g.</param>
    /// <param name="orientation">Body to world, as the world view draws it.</param>
    /// <param name="gravity">World frame, m/s², the way gravity pulls (0, −9.8, 0) on Earth.</param>
    public static Vector3 FromSpecificForce(Vector3 specificForce, Quaternion orientation, Vector3 gravity)
    {
        var worldAcceleration = Vector3.Transform(specificForce, orientation) + gravity;
        return Clamp(worldAcceleration * (PerG / StandardG));
    }

    /// <summary>Limits a displacement to <see cref="Max"/> on each axis; zero if it is not finite.</summary>
    public static Vector3 Clamp(Vector3 v) =>
        float.IsFinite(v.X + v.Y + v.Z) ? Vector3.Clamp(v, new Vector3(-Max), new Vector3(Max)) : Vector3.Zero;
}
