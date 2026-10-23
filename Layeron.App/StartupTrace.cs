using System;
using System.Diagnostics;
using System.IO;

namespace Layeron.App;

/// <summary>
/// Opt-in startup timeline for diagnosing how long each phase of a cold start takes.
/// <para>
/// Set <c>LAYERON_STARTUP_TRACE=1</c> before launching the app. Each mark appends one CSV row to
/// <c>%TEMP%\layeron-startup.csv</c> (override with <c>LAYERON_STARTUP_TRACE_FILE</c>) containing:
/// milliseconds since process start, milliseconds since runtime start, mark name, working set, thread
/// count and total CPU time. The marks are no-ops when the variable is not set, so they can stay in
/// the startup path permanently.
/// </para>
/// <para>
/// A mark name is a phase, not a log line. <c>main-entry</c> through <c>window-opened</c> cover the
/// path to a visible window; <c>layout-restored</c>, <c>first-layout-pass</c> and the
/// <c>dispatcher-*</c> marks cover the path to an interactive window. <c>dockable-built</c> and
/// <c>plugin-content-built</c> attribute the layout cost to individual panes.
/// </para>
/// </summary>
internal static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly bool On = Environment.GetEnvironmentVariable("LAYERON_STARTUP_TRACE") == "1";
    private static readonly string Path =
        Environment.GetEnvironmentVariable("LAYERON_STARTUP_TRACE_FILE")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "layeron-startup.csv");

    private static double _offsetMs;

    /// <summary>True when the timeline is being recorded; use it to skip measuring work on the normal path.</summary>
    public static bool Enabled => On;

    public static void Begin()
    {
        if (!On)
            return;
        try
        {
            _offsetMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds
                        - Clock.Elapsed.TotalMilliseconds;
        }
        catch
        {
            _offsetMs = 0;
        }
    }

    public static void Mark(string name)
    {
        if (!On)
            return;
        var sinceProcess = _offsetMs + Clock.Elapsed.TotalMilliseconds;
        var line = $"{(long)sinceProcess},{Clock.Elapsed.TotalMilliseconds:F1},{name},{Environment.WorkingSet / (1024 * 1024)}MB,{ThreadCount()},{CpuMs()}ms";
        try
        {
            File.AppendAllText(Path, line + Environment.NewLine);
        }
        catch
        {
            // measurement must never break startup
        }

        // Do not touch Console before the parent console is attached: doing so would initialise
        // Console.Out against "no console" and keep every later write on that dead stream.
        if (Program.HasConsole)
            Console.WriteLine($"[startup] {line}");
    }

    private static int ThreadCount()
    {
        try
        {
            return Process.GetCurrentProcess().Threads.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static long CpuMs()
    {
        try
        {
            return (long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        }
        catch
        {
            return 0;
        }
    }
}
