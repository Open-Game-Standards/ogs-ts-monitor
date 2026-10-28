using System.Numerics;

namespace Ogs.Telemetry.Monitor;

/// <summary>
/// Orientation readout in the OGS-TS frame (left-handed: Z forward, X right,
/// Y up), as the reference receiver prints it. ZYX Tait-Bryan angles from the
/// rotated basis vectors: forward f = q·(0,0,1), right r = q·(1,0,0) →
/// yaw = atan2(f.X, f.Z) (0 = +Z, turning right positive), pitch = asin(f.Y)
/// (nose up positive), roll = asin(−r.Y / cos pitch) (right side down
/// positive; degenerate only at ±90° pitch).
/// </summary>
public static class Euler
{
    const float Rad2Deg = 180f / MathF.PI;

    public static (float Yaw, float Pitch, float Roll) FromQuaternion(Quaternion q)
    {
        var f = Vector3.Transform(Vector3.UnitZ, q);
        var r = Vector3.Transform(Vector3.UnitX, q);
        float yaw = MathF.Atan2(f.X, f.Z) * Rad2Deg;
        float pitch = MathF.Asin(Clamp1(f.Y)) * Rad2Deg;
        float cosPitch = MathF.Cos(pitch / Rad2Deg);
        float roll = MathF.Abs(cosPitch) < 1e-3f ? 0f : MathF.Asin(Clamp1(-r.Y / cosPitch)) * Rad2Deg;
        return (yaw + 0f, pitch + 0f, roll + 0f); // +0f folds −0.0
    }

    static float Clamp1(float v) => v < -1f ? -1f : v > 1f ? 1f : v;
}
