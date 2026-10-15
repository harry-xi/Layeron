using Layeron.Plugins.Onnx.Api;
using Microsoft.ML.OnnxRuntime;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// Owns the model files: scanning, loading, dropping, and remembering what failed.
/// <para>
/// Separate from the inference path on purpose. Loading a model costs a native session and touches the file,
/// while running one is a hot call; keeping them apart is what lets a rescan happen without serialising
/// behind an inference that is already in flight.
/// </para>
/// </summary>
internal sealed class OnnxModelStore : IDisposable
{
    /// <summary>
    /// Folder name under the plugin's data directory where the bundled demo models are deployed, and the one
    /// folder that always exists - so a first run has something to show without the user finding a model.
    /// </summary>
    public const string DemoFolderName = "models";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, OnnxModelEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Raised when the loaded set changed in a way worth redrawing.</summary>
    public event Action? Changed;

    /// <summary>Currently loaded models, ordered by id so the panel does not reshuffle between scans.</summary>
    public IReadOnlyList<OnnxModelInfo> Models
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .Select(e => e.Info)
                    .OrderBy(i => i.Id, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }

    /// <summary>Models that could not be loaded, with the reason, for the settings page to report.</summary>
    public IReadOnlyList<(string Path, string Reason)> Failures
    {
        get
        {
            lock (_gate)
                return _failed.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Rescans the folders, loading what is new or newer and dropping what is gone. Returns the number of
    /// models whose state changed.
    /// </summary>
    public int Rescan(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        // Same model id in two folders: the earlier folder wins, so a user folder listed before the bundled
        // one can shadow a demo model rather than being shadowed by it.
        var found = new Dictionary<string, (string Path, DateTime Stamp)>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                continue;

            foreach (var file in EnumerateModels(folder))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                found.TryAdd(id, (file, File.GetLastWriteTimeUtc(file)));
            }
        }

        var changed = 0;
        lock (_gate)
        {
            foreach (var id in _entries.Keys.Where(id => !found.ContainsKey(id)).ToList())
            {
                _entries[id].Dispose();
                _entries.Remove(id);
                changed++;
            }

            foreach (var (id, target) in found)
            {
                if (_entries.TryGetValue(id, out var existing))
                {
                    if (existing.Path == target.Path && existing.LastWriteTimeUtc == target.Stamp)
                        continue;

                    existing.Dispose();
                    _entries.Remove(id);
                }

                // A file that already failed is retried only once its timestamp changes. Without this, a single
                // unusable model in the folder costs a full load attempt on every rescan.
                if (_failed.TryGetValue(target.Path, out _))
                    continue;

                if (Load(id, target.Path, target.Stamp) is { } entry)
                {
                    _entries[id] = entry;
                    changed++;
                }
            }
        }

        if (changed > 0)
            Changed?.Invoke();

        return changed;
    }

    /// <summary>The loaded entry for an id, name or path; null when nothing matches.</summary>
    public OnnxModelEntry? Find(string idOrNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(idOrNameOrPath))
            return null;

        lock (_gate)
        {
            if (_entries.TryGetValue(idOrNameOrPath, out var byId))
                return byId;

            foreach (var entry in _entries.Values)
            {
                if (string.Equals(entry.Info.Name, idOrNameOrPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Path, idOrNameOrPath, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_gate)
        {
            foreach (var entry in _entries.Values)
                entry.Dispose();
            _entries.Clear();
        }
    }

    private static IEnumerable<string> EnumerateModels(string folder)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*.onnx", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            // An unreadable folder shows up as an empty scan instead of an exception through plugin activation;
            // the settings page is where a user would notice it.
            yield break;
        }

        foreach (var file in files)
            yield return file;
    }

    private OnnxModelEntry? Load(string id, string path, DateTime stamp)
    {
        InferenceSession? session = null;
        try
        {
            var options = new SessionOptions
            {
                // One inter-op thread: the runner already parallelises by walking a volume in tiles, and letting
                // every session spin up its own pool as well makes a whole-volume run slower, not faster.
                InterOpNumThreads = 1
            };

            // CPU is stated rather than implied. A GPU provider is a separate package with its own native
            // payload, so gaining one is a deployment decision, not something this plugin should assume on the
            // user's behalf.
            options.AppendExecutionProvider_CPU();

            session = new InferenceSession(path, options);

            var inputs = session.InputMetadata
                .Select(pair => OnnxTensorAdapter.ToSpec(pair.Key, pair.Value))
                .ToList();
            var outputs = session.OutputMetadata
                .Select(pair => OnnxTensorAdapter.ToSpec(pair.Key, pair.Value))
                .ToList();

            var info = new OnnxModelInfo
            {
                Id = id,
                Name = ReadName(session) ?? id,
                Description = session.ModelMetadata?.Description,
                Path = path,
                Inputs = inputs,
                Outputs = outputs,
                Providers = ReadProviders(session),
                SizeBytes = new FileInfo(path).Length,
                SpatialExtent = OnnxTensorAdapter.InferSpatialExtent(inputs.FirstOrDefault())
            };

            return new OnnxModelEntry(path, session, info, stamp);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            lock (_gate)
                _failed[path] = ex.Message;
            return null;
        }
    }

    private static string? ReadName(InferenceSession session)
    {
        var name = session.ModelMetadata?.GraphName;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Names the providers the session actually adopted. ORT 1.31 has no flat "assigned providers" accessor,
    /// but the devices bound to the inputs answer the same question - and the honest answer is what a panel
    /// should show, because asking for a provider is not the same as getting it.
    /// </summary>
    private static IReadOnlyList<string> ReadProviders(InferenceSession session)
    {
        try
        {
            var names = session.GetEpDeviceForInputs()
                .Select(device => device.EpName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (names.Count > 0)
                return names;
        }
        catch (Exception)
        {
            // A binding that cannot answer is not a reason to call the model unusable.
        }

        return ["CPUExecutionProvider"];
    }
}
