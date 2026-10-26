using Layeron.Big3DArray;
using Layeron.Plugin.Abstractions;
using NumSharp;

namespace Layeron.Scripting.Abstractions;

/// <summary>
/// The object a script sees as <c>this</c>/globals: the whole API surface a script gets.
/// <para>
/// It serves two jobs, and they are the two ways a script is useful. A script can <em>compute on the
/// current data</em> - read the volume, report progress and results, write a mask or a new volume back -
/// and it can <em>extend the application</em>, by contributing UI exactly as a compiled plugin does. The
/// compute helpers are sugar over <see cref="IPluginServices"/>; there is deliberately no second data
/// model behind them.
/// </para>
/// <para>
/// This type must live in an assembly loaded once for both sides. Roslyn compiles a script into an
/// assembly it loads into the default context, whereas the script host plugin runs inside its own
/// collectible context; if the globals type travelled with the plugin there would be two copies of it
/// and the cast Roslyn performs on the globals argument would fail. This assembly is the scripting
/// layer's shared contract, deferred to the default context by the plugin loader, so both sides bind
/// to one <see cref="ScriptGlobals"/> - the same "host standard type" rule extension points follow
/// (design-notes/PLUGIN_SYSTEM.md §7, §10).
/// </para>
/// </summary>
public sealed class ScriptGlobals
{
    /// <summary>
    /// Revision of the main volume when this instance was created; the baseline a stale-result warning is
    /// measured against. It never blocks a write - see <see cref="WarnIfVolumeReplaced"/>.
    /// </summary>
    private long _baselineVersion;

    public ScriptGlobals(IPluginContext context, string scriptPath, ScriptRunContext? run = null)
    {
        Plugin = context;
        ScriptPath = scriptPath;
        Run = run ?? ScriptRunContext.None;
        _baselineVersion = context.Services.Volumes.Version;
    }

    /// <summary>The script's plugin context: registrations made here unload with the script.</summary>
    public IPluginContext Plugin { get; }

    /// <summary>Path of the running script, handy for log messages.</summary>
    public string ScriptPath { get; }

    /// <summary>Per-execution state: cancellation, reporting, UI marshalling.</summary>
    public ScriptRunContext Run { get; }

    /// <summary>Host services (volume, masks, annotations, preview, logging).</summary>
    public IPluginServices Host => Plugin.Services;

    // ---------------------------------------------------------------------
    //  Reading the current data
    // ---------------------------------------------------------------------

    /// <summary>
    /// The current main volume, or null when no data is loaded. Pattern-match the element type
    /// (<c>is ReadonlyBig3DArray&lt;byte&gt;</c> / <c>&lt;ushort&gt;</c>) and then work with the core types:
    /// the block API is the fast path, the indexer is there for probing single voxels.
    /// </summary>
    public ReadonlyBig3DArray? Volume => Host.Volumes.Current;

    /// <summary>Revision of the main volume; it advances whenever the volume is replaced.</summary>
    public long VolumeVersion => Host.Volumes.Version;

    /// <summary>
    /// The main volume together with its revision, taken in one step. Prefer this over reading
    /// <see cref="Volume"/> and <see cref="VolumeVersion"/> separately: a result derived from an older
    /// revision belongs to data that is no longer on screen and must be discarded, not published.
    /// </summary>
    public VolumeSnapshot Snapshot() => Host.Volumes.Snapshot();

    /// <summary>Crosshair position, so a script can default to whatever slice the user is looking at.</summary>
    public (int X, int Y, int Z) Crosshair => (Host.Volumes.CurrentX, Host.Volumes.CurrentY, Host.Volumes.CurrentZ);

    /// <summary>
    /// Gray-level histogram of the current volume (one bin per value, spanning the element type's range),
    /// or null when no data is loaded. Computed by the host in a single streamed pass.
    /// </summary>
    public long[]? Histogram() => Host.Volumes.ComputeHistogram(Cancellation);

    /// <summary>
    /// Copies a rectangular region of the current volume into an <see cref="NDArray"/> shaped
    /// <c>[z, y, x]</c>, so <c>roi[lz, ly, lx]</c> is the voxel at <c>(x0 + lx, y0 + ly, z0 + lz)</c>.
    /// This is the bridge to numeric code: extract a region, compute with it, then hand the result back
    /// through <see cref="AddMask"/> or <see cref="ReplaceVolume"/>.
    /// <para>
    /// The region must lie entirely inside the volume; the copy is block-wise, so it costs one pass over
    /// the region and no per-voxel bounds checks.
    /// </para>
    /// </summary>
    public NDArray ReadRoi(int x0, int y0, int z0, int sizeX, int sizeY, int sizeZ)
    {
        var volume = Volume ?? throw new InvalidOperationException("没有加载数据。");

        return volume switch
        {
            ReadonlyBig3DArray<byte> v8 => np.array(v8.ReadRoi(x0, y0, z0, sizeX, sizeY, sizeZ)),
            ReadonlyBig3DArray<ushort> v16 => np.array(v16.ReadRoi(x0, y0, z0, sizeX, sizeY, sizeZ)),
            _ => throw new NotSupportedException($"不支持的体素类型：{volume.ElementType}。")
        };
    }

    /// <summary>The current XY slice at <paramref name="z"/> as an <see cref="NDArray"/> shaped <c>[y, x]</c>.</summary>
    public NDArray ReadXYSlice(int z)
    {
        var volume = Volume ?? throw new InvalidOperationException("没有加载数据。");

        return volume switch
        {
            ReadonlyBig3DArray<byte> v8 => np.array(v8.GetXYSlice(z)),
            ReadonlyBig3DArray<ushort> v16 => np.array(v16.GetXYSlice(z)),
            _ => throw new NotSupportedException($"不支持的体素类型：{volume.ElementType}。")
        };
    }

    // ---------------------------------------------------------------------
    //  Reporting
    // ---------------------------------------------------------------------

    /// <summary>Cancelled when this run is superseded: the user pressed 取消, or the editor tab closed.</summary>
    public CancellationToken Cancellation => Run.Cancellation;

    /// <summary>Reports a transient status line, shown next to the editor's progress bar.</summary>
    public void Status(string message) => Run.Status?.Invoke(message);

    /// <summary>Reports progress as a fraction in [0, 1]. Call it once per block, not per voxel.</summary>
    public void Progress(double fraction) => Run.Progress?.Invoke(fraction);

    /// <summary>
    /// Publishes a named result. It becomes a structured log entry, so the value shows up as a field in
    /// the log window and the entry can be copied individually - that is where a computation's output goes.
    /// </summary>
    public void Result(string label, object? value) =>
        Host.Log.Info("[{Script}] 结果 {Label} = {Value}", Path.GetFileName(ScriptPath), label, value);

    /// <summary>Writes a line to the host log, tagged with the script name as a structured value.</summary>
    public void Log(string message) =>
        Host.Log.Info("[{Script}] {Message}", Path.GetFileName(ScriptPath), message);

    // ---------------------------------------------------------------------
    //  Writing results back to the session
    // ---------------------------------------------------------------------

    /// <summary>
    /// Adds a mask layer. It is user data, so it stays after the run's registrations are torn down -
    /// exactly like a mask produced by a compiled plugin.
    /// <para>
    /// A mask is accepted even when the main volume has been replaced since this instance was created;
    /// the mismatch is only logged as a warning. The baseline is the instance's creation time, which
    /// cannot tell a stale result apart from a long-lived script re-reading the current volume, so
    /// blocking on it would reject good data. Dimension checking still applies as usual.
    /// </para>
    /// </summary>
    public IMaskLayer AddMask(string name, Big3DBitMap bitmap, MaskColor? color = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        WarnIfVolumeReplaced();

        IMaskLayer? layer = null;
        Run.Invoke(() =>
        {
            layer = Host.Masks.Add(name, bitmap, color ?? MaskColor.Yellow.WithAlpha(102));
            Host.Masks.NotifyContentChanged();
        });

        return layer!;
    }

    /// <summary>
    /// Replaces the main volume atomically - the same path a built-in load takes, so mask layers are
    /// validated against the new dimensions and the version bump invalidates stale background results.
    /// </summary>
    public void ReplaceVolume(ReadonlyBig3DArray volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        Run.Invoke(() =>
        {
            Host.Volumes.Replace(volume);
            // The script's own replacement becomes the new baseline, so masks built from it are not
            // warned about as stale.
            _baselineVersion = Host.Volumes.Version;
        });
    }

    /// <summary>Non-destructive preview overlays, for showing an intermediate result while working.</summary>
    public IPreviewChannel Preview => Host.Preview;

    /// <summary>
    /// Annotation layers and their point/line/polygon factories. Like masks, annotations are user data
    /// and survive the script run; use <see cref="ScriptRunContext.Invoke"/> when creating them from a
    /// background thread.
    /// </summary>
    public IAnnotationService Annotations => Host.Annotations;

    /// <summary>
    /// Logs a warning - it does not block anything - when the main volume has been replaced since this
    /// instance was created, because a result produced now may have been derived from the older revision.
    /// </summary>
    private void WarnIfVolumeReplaced()
    {
        if (Host.Volumes.Version != _baselineVersion)
        {
            Host.Log.Warn(
                "[{Script}] 主卷在本次运行期间已被替换，掩膜可能基于已过期数据。",
                Path.GetFileName(ScriptPath));
        }
    }

    // ---------------------------------------------------------------------
    //  Extending the application (the plugin path)
    // ---------------------------------------------------------------------

    /// <summary>Adds a menu entry under the scripts menu (the host's 插件 → 脚本 folder).</summary>
    public void Menu(string header, Action execute, Func<bool>? canExecute = null) =>
        Run.Invoke(() => Plugin.Registry.AddMenu(new MenuContribution
        {
            Path = ["脚本"],
            Header = header,
            Execute = execute,
            CanExecute = canExecute
        }));

    /// <summary>Adds a dockable panel built by the script.</summary>
    public void Tool(string id, string title, Func<object> contentFactory) =>
        Run.Invoke(() => Plugin.Registry.AddTool(new ToolContribution
        {
            Id = id,
            Title = title,
            ContentFactory = contentFactory
        }));

    /// <summary>
    /// Contributes a button to an extension point defined by another plugin - the reason a script can
    /// extend a compiled plugin, and vice versa.
    /// </summary>
    public void Contribute(string pointId, VolumeAction action) =>
        Run.Invoke(() => Plugin.Registry.Contribute(pointId, action));
}
