using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Ogs.Telemetry;
using Ogs.Telemetry.Schema;
using Ogs.Telemetry.Wire;

[assembly: AvaloniaTestApplication(typeof(Ogs.Telemetry.Monitor.Tests.TestAppBuilder))]

namespace Ogs.Telemetry.Monitor.Tests;

public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>
/// The stream log trims its oldest row on every refresh once it is full, and follows the newest
/// row. Following with <c>ListBox.ScrollIntoView</c> while the window was resized threw
/// "Invalid Arrange rectangle" out of the virtualizing panel and ended the app.
/// </summary>
public class LogFollowTests
{
    // a busy stream appends several rows per refresh, a varying number (one or two did not
    // trigger the fault, three or more did, depending on how the trimming lined up)
    static int RowsPerRefresh(int refresh) => 3 + refresh % 5;
    static readonly IPEndPoint Src = new(IPAddress.Parse("10.0.0.2"), 50001);

    static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    static void Event(StreamTracker tracker, uint id)
    {
        var e = new Event { Id = id, Name = "body.impact", Intensity = 0.5f };
        e.InitDirection().Z = -1;
        tracker.Ingest(CanonicalEncoder.EncodeDatagram(FrameType.Event,
            new EventFrame { Timestamp = id * 0.01, Events = new List<Event> { e } }), Src);
    }

    [AvaloniaFact]
    public void A_full_log_keeps_following_the_newest_row_through_resizes()
    {
        var tracker = new StreamTracker();
        var vm = new MonitorViewModel(tracker);
        var view = new MonitorView { DataContext = vm };
        var window = new Window { Content = view, Width = 1360, Height = 900 };
        window.Show();
        Pump(window);

        var list = view.FindControl<ListBox>("LogList")!;
        uint id = 1;

        // fill the log, then scroll to the bottom as a user watching the stream does
        for (; id <= 60; id++) { Event(tracker, id); vm.Refresh(); Pump(window); }
        list.FindDescendantOfType<ScrollViewer>()!.ScrollToEnd();
        Pump(window);

        // the log is full and trims row 0 each refresh; the window changes by a pixel before each refresh
        for (int i = 0; i < 500; i++)
        {
            for (int k = 0; k < RowsPerRefresh(i); k++) Event(tracker, id++);
            window.Width = 1360 + (i % 2);   // a resize is pending when the refresh scrolls
            vm.Refresh();
            Pump(window); // throws "Invalid Arrange rectangle" with ScrollIntoView
        }

        Assert.Equal(StreamTracker.MaxLogLines, list.ItemCount);
        var panel = (VirtualizingStackPanel)list.ItemsPanelRoot!;
        Assert.Equal(list.ItemCount - 1, panel.LastRealizedIndex); // still pinned to the newest row
    }
}
