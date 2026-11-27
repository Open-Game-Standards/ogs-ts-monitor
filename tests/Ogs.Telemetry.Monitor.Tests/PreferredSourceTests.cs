using System.Net;
using Ogs.Telemetry.Schema;
using Ogs.Telemetry.Wire;

namespace Ogs.Telemetry.Monitor.Tests;

public sealed class PreferredSourceTests : IDisposable
{
    static readonly IPEndPoint Alpha = new(IPAddress.Loopback, 50021);
    static readonly IPEndPoint Beta = new(IPAddress.Loopback, 50022);

    readonly TelemetryReceiver _receiver =
        new(TelemetryReceiver.LoopbackGroup, 41361, IPAddress.Loopback);
    readonly MonitorViewModel _vm;
    double _timestamp;

    public PreferredSourceTests() => _vm = new MonitorViewModel(new StreamTracker().Attach(_receiver));

    public void Dispose() => _receiver.Dispose();

    void Stream(IPEndPoint from, string game)
    {
        _receiver.Inject(from, CanonicalEncoder.EncodeDatagram(FrameType.Discovery, new DiscoveryFrame
        {
            SchemaVersion = 1,
            GameName = game,
            SessionStartUnixUs = 1,
            Objects = new List<ObjectDescriptor> { new() { Name = "car", Primary = true } },
        }));
        for (int i = 0; i < 3; i++)
            _receiver.Inject(from, CanonicalEncoder.EncodeDatagram(FrameType.Motion, new MotionFrame
            {
                Timestamp = _timestamp += 0.01,
                Objects = new List<MotionObject> { new() { Name = "car", Orientation = { W = 1 } } },
            }));
    }

    string Shown()
    {
        _vm.Refresh();
        return _vm.GameName;
    }

    [Fact]
    public void The_preferred_sender_is_shown_even_while_another_streams()
    {
        Stream(Alpha, "alpha");
        Assert.Equal("alpha", Shown());
        Stream(Beta, "beta");

        _vm.PreferredSource = Beta.ToString();
        Assert.Equal("beta", Shown());

        _vm.PreferredSource = Alpha.ToString();
        Assert.Equal("alpha", Shown());
    }

    [Fact]
    public void An_object_picked_by_hand_stays_until_the_preferred_sender_changes()
    {
        Stream(Alpha, "alpha");
        Stream(Beta, "beta");
        _vm.PreferredSource = Alpha.ToString();
        Shown();

        _vm.SelectedObject = _vm.Objects.Single(o => o.Key.StartsWith(Beta.ToString()));
        Assert.Equal("beta", Shown());
        _vm.PreferredSource = Alpha.ToString();
        Assert.Equal("beta", Shown());

        _vm.PreferredSource = Beta.ToString();
        _vm.PreferredSource = Alpha.ToString();
        Assert.Equal("alpha", Shown());
    }

    [Fact]
    public void A_preferred_sender_that_is_absent_leaves_the_monitor_choosing()
    {
        _vm.PreferredSource = Beta.ToString();
        Stream(Alpha, "alpha");

        Assert.Equal("alpha", Shown());
    }
}
