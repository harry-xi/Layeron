using System.Text.Json;
using Layeron.Big3DArray;
using Layeron.Plugins.Onnx.Api;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// The runner's durable configuration: which folders to scan, and how each model is bound to Layeron data.
/// <para>
/// Bindings are stored per model id because a model's signature decides how data must be shaped, and a model
/// the runner has never seen needs a default rather than an error. Keeping them here instead of in the panel
/// is what makes a binding survive the plugin unloading, which is the only way hot reload is usable.
/// </para>
/// </summary>
internal sealed class OnnxSettings
{
    /// <summary>Setting keys, declared once so the settings page and the reader cannot drift apart.</summary>
    internal static class Keys
    {
        public const string ExtraFolders = "onnx.folders";
        public const string TileOverlap = "onnx.tiles.overlap";
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OnnxBinding> _bindings = new(StringComparer.Ordinal);

    public OnnxSettings(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "onnx.settings.json");
        Load();
    }

    /// <summary>Raised after a binding changed, so a panel showing that model can refresh without polling.</summary>
    public event Action? BindingsChanged;

    /// <summary>Folders the user added, on top of the workspace and plugin data folders.</summary>
    public IReadOnlyList<string> ExtraFolders =>
        Read(Keys.ExtraFolders, string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// <c>models</c> folder inside the plugin's own install directory, set at activation. Scanned like any other
    /// folder, which is what lets a user drop a model next to the plugin instead of into their workspace.
    /// </summary>
    public string? PluginModelsFolder { get; set; }

    /// <summary>Voxel overlap between whole-volume tiles.</summary>
    public int TileOverlap => (int)Read(Keys.TileOverlap, 4L);

    /// <summary>
    /// Folders scanned for models, in precedence order: user folders first, then the plugin's own folder, then a
    /// workspace folder, then the plugin's data folder. Order matters because the first folder to declare a model id
    /// owns it.
    /// </summary>
    public IReadOnlyList<string> ModelFolders(string pluginDataDirectory, string? workspaceRoot)
    {
        var candidates = new List<string>(ExtraFolders);
        if (!string.IsNullOrWhiteSpace(PluginModelsFolder))
            candidates.Add(PluginModelsFolder);
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
            candidates.Add(Path.Combine(workspaceRoot, OnnxModelStore.ModelsFolderName));
        candidates.Add(Path.Combine(pluginDataDirectory, OnnxModelStore.ModelsFolderName));

        // De-duplicated by full path so a user who lists the default folder explicitly does not make it appear
        // twice in the settings page or get scanned twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (var candidate in candidates)
        {
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                // A malformed entry is the user's typo, not a reason to fail startup; the settings page
                // validates new entries before they are stored.
                continue;
            }

            if (seen.Add(full))
                folders.Add(full);
        }

        return folders;
    }

    /// <summary>
    /// Validates and stores a new folder list. Returns null on success, or the first problem found - validating
    /// on write is what keeps <see cref="ModelFolders"/> free of error reporting.
    /// </summary>
    public string? SetExtraFolders(string text)
    {
        foreach (var folder in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (!Directory.Exists(Path.GetFullPath(folder)))
                    return $"目录不存在：{folder}";
            }
            catch (Exception)
            {
                return $"无效路径：{folder}";
            }
        }

        Set(Keys.ExtraFolders, text);
        return null;
    }

    /// <summary>The stored binding for a model, or a default derived from its signature.</summary>
    public OnnxBinding BindingFor(OnnxModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return _bindings.TryGetValue(model.Id, out var binding)
            ? binding.Clamped(model)
            : OnnxBinding.DefaultFor(model);
    }

    /// <summary>Stores a binding and notifies whoever is showing that model.</summary>
    public void SetBinding(OnnxBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _bindings[binding.ModelId] = binding;
        Save();
        BindingsChanged?.Invoke();
    }

    /// <summary>Backs the settings page's <c>ValueGetter</c>.</summary>
    public object? Get(string key) => key == Keys.ExtraFolders
        ? string.Join(';', ExtraFolders)
        : _values.GetValueOrDefault(key);

    /// <summary>Backs the settings page's <c>ValueSetter</c>.</summary>
    public void Set(string key, object? value)
    {
        _values[key] = value;
        Save();
    }

    private T Read<T>(string key, T fallback)
    {
        var raw = _values.GetValueOrDefault(key);
        return raw switch
        {
            null => fallback,
            T typed => typed,
            // The host's numeric rows hand back long or double depending on the declared kind, so a narrow read
            // has to survive the wider type rather than throw.
            _ => ConvertTo(raw, fallback)
        };
    }

    private static T ConvertTo<T>(object raw, T fallback)
    {
        try
        {
            return (T)Convert.ChangeType(raw, typeof(T));
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private void Load()
    {
        var file = ReadFile();
        foreach (var pair in file.Values)
            _values[pair.Key] = pair.Value;
        foreach (var binding in file.Bindings)
            _bindings[binding.ModelId] = binding;
    }

    private SettingsFile ReadFile()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new SettingsFile();
            return JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_filePath), SerializerOptions)
                   ?? new SettingsFile();
        }
        catch (Exception)
        {
            // An unreadable settings file is not worth failing plugin activation over: the runner starts on
            // defaults and the next save replaces the file.
            return new SettingsFile();
        }
    }

    private void Save()
    {
        try
        {
            var file = new SettingsFile();
            foreach (var pair in _values)
                file.Values[pair.Key] = pair.Value;
            file.Bindings.AddRange(_bindings.Values);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(file, SerializerOptions));
        }
        catch (Exception)
        {
            // Losing a preference is not worth an unhandled exception on the UI thread: the value still applies
            // for this session, because it is already in the dictionary.
        }
    }

    /// <summary>On-disk shape, kept as a plain DTO so the file format is visible in one place.</summary>
    private sealed class SettingsFile
    {
        public Dictionary<string, object?> Values { get; set; } = new(StringComparer.Ordinal);

        public List<OnnxBinding> Bindings { get; set; } = [];
    }
}

/// <summary>
/// How one model is bound to Layeron data: what the panel and the service do unless a request overrides it.
/// </summary>
internal sealed class OnnxBinding
{
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Which plane a slice-mode model scans along.</summary>
    public OnnxSlicePlane Plane { get; set; } = OnnxSlicePlane.XY;

    /// <summary>Low end of the source range, or null for the identity mapping.</summary>
    public float? SourceMin { get; set; }

    /// <summary>High end of the source range.</summary>
    public float? SourceMax { get; set; }

    /// <summary>Low end of the model's output range, used to display and to scale a produced volume.</summary>
    public float OutputMin { get; set; }

    /// <summary>High end of the model's output range.</summary>
    public float OutputMax { get; set; } = 1f;

    /// <summary>Normalised output level above which the preview highlights the voxel as structure.</summary>
    public double Threshold { get; set; } = 0.6d;

    /// <summary>Element type a whole-volume run produces.</summary>
    public PixelElementType ElementType { get; set; } = PixelElementType.UInt16;

    /// <summary>
    /// True when the model is run plane by plane rather than over a volume block. A property of the binding
    /// rather than of the model, because the same file can be used either way.
    /// </summary>
    public bool SliceMode { get; set; } = true;

    /// <summary>
    /// The binding a model gets the first time it is seen: a signature with a depth axis is a volume model and
    /// anything shallower is a slice model, so the common case needs no configuration.
    /// </summary>
    public static OnnxBinding DefaultFor(OnnxModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var rank = model.PrimaryInput?.Shape.Count ?? 0;
        return new OnnxBinding { ModelId = model.Id, SliceMode = rank < 5 };
    }

    /// <summary>
    /// Constrains a stored binding to what the model now accepts. A model file can be replaced by a different
    /// graph at the same path, and a stale plane or a tile size the model cannot take would then produce
    /// silently wrong geometry - so the signature always gets the last word.
    /// </summary>
    public OnnxBinding Clamped(OnnxModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var rank = model.PrimaryInput?.Shape.Count ?? 0;
        return new OnnxBinding
        {
            ModelId = ModelId,
            Plane = Plane,
            SourceMin = SourceMin,
            SourceMax = SourceMax,
            OutputMin = OutputMin,
            OutputMax = OutputMax > OutputMin ? OutputMax : OutputMin + 1f,
            Threshold = Math.Clamp(Threshold, 0d, 1d),
            ElementType = ElementType,
            SliceMode = rank < 5
        };
    }

    /// <summary>The input mapping this binding describes, or the identity when it names no usable range.</summary>
    public OnnxInputMapping ToMapping() => SourceMin is { } min && SourceMax is { } max && max > min
        ? new OnnxInputMapping { SourceMin = min, SourceMax = max, TargetMin = 0f, TargetMax = 1f }
        : OnnxInputMapping.Identity;

    /// <summary>
    /// Fraction of the output range where the switch between "flat" and "structure" sits. A model whose output
    /// is centred on a neutral value - which is what band-pass models emit - is symmetric around it, so the
    /// low end of the range is the neutral level and the threshold's distance from the high end is the
    /// distance from the low end.
    /// </summary>
    public double ResolveThreshold(double rangeMin, double rangeMax)
    {
        var centre = rangeMin + (rangeMax - rangeMin) / 2d;
        return centre + (rangeMax - centre) * Math.Clamp(Threshold, 0d, 1d);
    }
}
