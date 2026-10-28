namespace Ogs.Telemetry.Monitor.Tests;

public class ViewModelTests
{
    [Fact]
    public void Attitude_mode_buttons_select_exactly_one()
    {
        var vm = new MonitorViewModel(new StreamTracker());
        Assert.True(vm.IsChaseMode);

        vm.IsHorizonMode = true;
        Assert.Equal(AttitudeMode.Horizon, vm.Mode);
        Assert.True(vm.UseHorizon);
        Assert.False(vm.IsChaseMode);

        // from the horizon, Chase is still one click away
        vm.IsChaseMode = true;
        Assert.Equal(AttitudeMode.Chase, vm.Mode);
        Assert.True(vm.Chase);
        Assert.True(vm.UseAxes);

        vm.IsWorldMode = true;
        Assert.False(vm.Chase);
        Assert.True(vm.UseAxes);

        // unchecking the selected mode is refused: one stays on
        vm.IsWorldMode = false;
        Assert.Equal(AttitudeMode.World, vm.Mode);
    }
}
