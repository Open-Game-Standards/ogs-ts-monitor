using System.Globalization;
using Avalonia.Threading;

namespace OgsTsMonitor;

/// <summary>
/// Keeps the monitor alive through a fault on the UI thread. A receiver on a
/// rig must not end because one layout pass threw (a window docked or
/// resized to an extent a virtualizing panel cannot arrange raises
/// "Invalid Arrange rectangle"); the receive thread and the tracker are
/// independent of the UI, so the stream keeps being tracked and the next
/// layout pass recovers. Every fault is appended to a log file instead of
/// being lost, and a fault that repeats does not flood it.
/// </summary>
public static class CrashGuard
{
    const int MaxLoggedPerSession = 50;
    static int _logged;

    /// <summary>Where faults are written (created on first fault).</summary>
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OgsTsMonitor", "faults.log");

    public static void Install()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Record("UI thread", e.Exception);
            e.Handled = true;
        };
        // faults off the UI thread cannot be recovered here, but are recorded
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
