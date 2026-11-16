using Ogs.Telemetry.Monitor;
using Xunit;

namespace Ogs.Telemetry.Monitor.Tests;

public class EulerTests
{
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(30f, 0f, 0f)]
    [InlineData(-120f, 0f, 0f)]
    [InlineData(0f, 20f, 0f)]
    [InlineData(0f, -35f, 0f)]
    [InlineData(0f, 0f, 25f)]
    [InlineData(0f, 0f, -40f)]
    [InlineData(45f, 15f, -20f)]
    [InlineData(-100f, -30f, 50f)]
    [InlineData(40f, 10f, 150f)]       // rolled past 90: asin(-r.y / cos pitch) folded these
    [InlineData(-100f, -30f, 170f)]
    [InlineData(119f, 12f, -47f)]
    [InlineData(-96f, -35f, 6f)]
    [InlineData(90f, 20f, 0f)]         // a pure nose-up at heading 90 has no roll
    [InlineData(-170f, 60f, -175f)]
    public void ToQuaternion_round_trips_through_FromQuaternion(float yaw, float pitch, float roll)
    {
        var (y, p, r) = Euler.FromQuaternion(Euler.ToQuaternion(yaw, pitch, roll));
        Assert.Equal(yaw, y, 2);
        Assert.Equal(pitch, p, 2);
        Assert.Equal(roll, r, 2);
    }
}
