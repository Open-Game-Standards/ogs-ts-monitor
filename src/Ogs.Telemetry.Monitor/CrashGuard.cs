using System.Globalization;
using Avalonia.Threading;

namespace Ogs.Telemetry.Monitor;

/// <summary>
/// Keeps an app that hosts the monitor views alive through a fault on the UI
/// thread. A receiver on a rig must not end because one layout pass threw (a
/// window docked or resized to an extent a virtualizing panel cannot arrange
/// raises "Invalid Arrange rectangle"); the receive thread and the tracker are
/// independent of the UI, so the stream keeps being tracked and the next
/// layout pass recovers. Every fault is appended to a log file instead of
/// being lost, and a fault that repeats does not flood it. The hosting app
/// calls <see cref="Install"/> once at startup.
/// </summary>
public static class CrashGuard
{
    const int MaxLoggedPerSession = 50;
    static int _logged;

    /// <summary>Where faults are written (created on first fault).</summary>
    public static string LogPath { get; private set; } = DefaultPath("OgsTsMonitor");

    static string DefaultPath(string appName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        appName, "faults.log");

    /// <summary>Names the log (<c>%LOCALAPPDATA%\&lt;appName&gt;\faults.log</c>, or
    /// <c>faults.log</c> in <paramref name="directory"/>) and resets the cap.</summary>
    public static void ConfigureLog(string appName, string? directory = null)
    {
        LogPath = directory is null ? DefaultPath(appName) : Path.Combine(directory, "faults.log");
        Interlocked.Exchange(ref _logged, 0);
    }

    /// <summary>Handles UI-thread faults (logged, then the app carries on) and logs
    /// faults off the UI thread, which cannot be recovered here.</summary>
    public static void Install(string appName)
    {
        ConfigureLog(appName);
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Record("UI thread", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Record("unhandled", e.ExceptionObject as Exception);
    }

    /// <summary>Appends a fault to <see cref="LogPath"/>; never throws.</summary>
    public static void Record(string where, Exception? ex)
    {
        try
        {
            if (Interlocked.Increment(ref _logged) > MaxLoggedPerSession) return;
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTime.Now.ToString("s", CultureInfo.InvariantCulture)} [{where}] {ex}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // logging must never be the second fault
        }
    }
}
