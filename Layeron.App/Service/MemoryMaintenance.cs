using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Layeron.App.Service;

/// <summary>
/// One point-in-time view of the memory the process is holding. Kept as plain numbers so the menu
/// action can log the before/after pair without depending on a profiler.
/// </summary>
internal readonly record struct MemoryUsage(
    long ManagedBytes,
    long CommittedBytes,
    long WorkingSetBytes,
    long PrivateBytes)
{
    public static MemoryUsage Capture()
    {
        using var process = Process.GetCurrentProcess();

        return new MemoryUsage(
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetGCMemoryInfo().TotalCommittedBytes,
            process.WorkingSet64,
            process.PrivateMemorySize64);
    }
}

/// <summary>
/// The result of a forced collection plus a working-set trim: what moved, and whether the trim
/// itself succeeded (it is a Windows-only operation).
/// </summary>
internal readonly record struct MemoryTrimResult(
    MemoryUsage Before,
    MemoryUsage After,
    bool WorkingSetTrimmed,
    string? Note)
{
    public long ManagedReleased => Before.ManagedBytes - After.ManagedBytes;

    public long WorkingSetReleased => Before.WorkingSetBytes - After.WorkingSetBytes;
}

/// <summary>
/// Manual "give the memory back" action behind the <c>Tools → 回收内存 (GC)</c> menu entry.
/// <para>
/// <see cref="GC.Collect()"/> only makes managed objects unreachable. Two further things decide
/// whether the operating system sees the memory drop:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// The GC may keep freed segments committed for reuse (<c>GCSettings.RetainVM</c>). Turning that off
/// and asking for LOH compaction lets the runtime release them instead of caching them.
/// </description>
/// </item>
/// <item>
/// <description>
/// Even released pages can stay resident in the process working set. On Windows,
/// <c>EmptyWorkingSet</c> pushes those pages onto the standby list, which is what makes Task Manager's
/// "Memory (private working set)" drop immediately. It does <b>not</b> reduce private/committed bytes;
/// the pages come back on the next access and the call is therefore only appropriate for an explicit,
/// user-requested diagnostic - never for a timer.
/// </description>
/// </item>
/// </list>
/// <para>
/// Native allocations (OpenGL buffers/textures, native libraries) are outside the GC's reach: they are
/// only returned when their owner releases them.
/// </para>
/// </summary>
internal static class MemoryMaintenance
{
    /// <summary>
    /// Trims the working set of the current process. Returns false for a non-Windows host or when the
    /// Win32 call is refused.
    /// </summary>
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>
    /// Forces a full blocking, compacting collection and then trims the working set. Returns the
    /// measurements for logging; never throws for a failed trim.
    /// </summary>
    public static MemoryTrimResult CollectAndTrim()
    {
        var before = MemoryUsage.Capture();

        // The GC keeps freed segments committed for reuse only when System.GC.RetainVM is on. The
        // desktop runtime defaults it to false (the runtime probe reports RetainVM=False), and there
        // is no managed setter, so nothing to do here - a deployment that turns it on would have to
        // change it in runtimeconfig.

        // LOH is collected only by a gen2 collection, and it is compacted only when asked. Surfaces
        // (vertex/index lists) are large arrays, so this is the step that actually reclaims them.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var trimmed = false;
        string? note = null;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                trimmed = EmptyWorkingSet(process.Handle);

                if (!trimmed)
                    note = $"EmptyWorkingSet failed (Win32 error {Marshal.GetLastWin32Error()})";
            }
            catch (Exception ex)
            {
                note = $"working-set trim failed: {ex.Message}";
            }
        }
        else
        {
            note = "working-set trim is Windows-only";
        }

        return new MemoryTrimResult(before, MemoryUsage.Capture(), trimmed, note);
    }
}
