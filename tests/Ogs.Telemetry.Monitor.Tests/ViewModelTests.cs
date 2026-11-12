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

public class CrashGuardTests
{
    [Fact]
    public void Faults_are_logged_to_the_named_file_and_the_log_is_capped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ogs-crashguard-" + Guid.NewGuid().ToString("N"));
        try
        {
            CrashGuard.ConfigureLog("TestApp", dir);
            Assert.Equal(Path.Combine(dir, "faults.log"), CrashGuard.LogPath);
            CrashGuard.Record("UI thread", new InvalidOperationException("Invalid Arrange rectangle."));
            var text = File.ReadAllText(CrashGuard.LogPath);
            Assert.Contains("[UI thread]", text);
            Assert.Contains("Invalid Arrange rectangle.", text);

            for (int i = 0; i < 200; i++) CrashGuard.Record("UI thread", new Exception("again"));
            Assert.InRange(File.ReadAllLines(CrashGuard.LogPath).Count(l => l.Contains("[UI thread]")), 1, 50);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            CrashGuard.ConfigureLog("OgsTsMonitor");
        }
    }

    [Fact]
    public void Recording_a_fault_never_throws_even_when_the_log_cannot_be_written()
    {
        // a file where the directory should be: the write fails, the call does not
        var file = Path.GetTempFileName();
        try
        {
            CrashGuard.ConfigureLog("TestApp", Path.Combine(file, "sub"));
            CrashGuard.Record("UI thread", new Exception("x"));
        }
        finally
        {
            File.Delete(file);
            CrashGuard.ConfigureLog("OgsTsMonitor");
        }
    }
}
