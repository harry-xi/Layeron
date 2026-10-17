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
    /// Conventional folder name for a plugin's own model files. The plugin uses it as the name of the folder it
    /// ships inside its own directory, and a caller looking for "the folder this plugin's models live in" should
    /// find the same one.
    /// </summary>
    public const string ModelsFolderName = "models";

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
            // A bundled model is not in any folder, so a folder scan must leave it alone rather than treat it as a
            // file that disappeared - and must not re-add it on the next scan either, which is what would make the
            // loaded set flap and fire a change event on every rescan.
            foreach (var id in _entries.Keys.Where(id => !_entries[id].IsEmbedded && !found.ContainsKey(id)).ToList())
            {
                _entries[id].Dispose();
                _entries.Remove(id);
                changed++;
            }

            foreach (var (id, target) in found)
            {
                if (_entries.TryGetValue(id, out var existing))
                {
                    if (existing.IsEmbedded)
                        continue;

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

    /// <summary>
    /// Loads the plugin's bundled models from memory, replacing whatever was loaded before.
    /// <para>
    /// Separate from <see cref="Rescan"/> because it is the opposite kind of source: a folder is re-read on demand
    /// and its models come and go with the files, while a bundled model is always there and is loaded once. Nothing
    /// is written to disk, so there is no deployment, no file timestamp to compare and no failure to report - a
    /// bundled model either loads or it does not exist.
    /// </para>
    /// </summary>
    /// <returns>The number of bundled models now loaded.</returns>
    public int UseEmbedded(IReadOnlyList<(string FileName, byte[] Bytes)> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var changed = 0;
        lock (_gate)
        {
            foreach (var id in _entries.Keys.Where(id => _entries[id].IsEmbedded).ToList())
            {
                _entries[id].Dispose();
                _entries.Remove(id);
                changed++;
            }

            foreach (var (fileName, bytes) in models)
            {
                // Ids are file names without extension, the same shape a scanned model gets, so a bundled model and
                // a folder model are named by the same rule and a user folder can shadow one by id.
                var id = Path.GetFileNameWithoutExtension(fileName);
                if (_entries.ContainsKey(id))
                    continue;

                if (Open(id, fileName, bytes) is { } entry)
                {
                    _entries[id] = entry;
                    changed++;
                }
            }
        }

        if (changed > 0)
            Changed?.Invoke();

        return _entries.Count(e => e.Value.IsEmbedded);
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
        try
        {
            var bytes = File.ReadAllBytes(path);
            return Open(id, path, bytes, stamp);
        }
        catch (Exception ex)
        {
            lock (_gate)
                _failed[path] = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Opens one model and reads its signature. The single place a session is created, so a file model and a
    /// bundled model are described by exactly the same code path.
    /// </summary>
    /// <param name="source">What to record as the model's origin - a file path, or the bundled resource name.</param>
    /// <param name="stamp">File timestamp for a scanned model; <c>default</c> for a bundled one.</param>
    private OnnxModelEntry? Open(string id, string source, byte[] bytes, DateTime stamp = default)
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

            session = new InferenceSession(bytes, options);

            var inputs = session.InputMetadata
                .Select(pair => OnnxTensorAdapter.ToSpec(pair.Key, pair.Value))
                .ToList();
            var outputs = session.OutputMetadata
                .Select(pair => OnnxTensorAdapter.ToSpec(pair.Key, pair.Value))
                .ToList();

            var embedded = stamp == default;
            var info = new OnnxModelInfo
            {
                Id = id,
                Name = ReadName(session) ?? id,
                Description = session.ModelMetadata?.Description,
                Path = source,
                IsEmbedded = embedded,
                Inputs = inputs,
                Outputs = outputs,
                Providers = ReadProviders(session),
                SizeBytes = bytes.LongLength,
                SpatialExtent = OnnxTensorAdapter.InferSpatialExtent(inputs.FirstOrDefault())
            };

            return new OnnxModelEntry(source, session, info, stamp);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            lock (_gate)
                _failed[source] = ex.Message;
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
