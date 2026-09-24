using System.Reflection;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugin.Host;

/// <summary>Options controlling where plugins are found and how strictly they are validated.</summary>
public sealed class PluginManagerOptions
{
    /// <summary>Folder scanned for released plugins.</summary>
    public string PluginDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Layeron", "plugins");

    /// <summary>
    /// Folder scanned for development builds, defaulting to a folder next to the executable. It is
    /// scanned first, so a locally built plugin shadows an installed one with the same id and a plain
    /// build is immediately testable without touching the installed folder.
    /// </summary>
    public string DevelopmentPluginDirectory { get; set; } = Path.Combine(
        AppContext.BaseDirectory, "plugins.dev");

    /// <summary>
    /// Reject descriptors whose declared API major version differs from
    /// <see cref="PluginApi.Version"/>. A plugin with no declared version is accepted and logged.
    /// </summary>
    public bool EnforceApiVersion { get; set; } = true;

    /// <summary>Attempt to collect the unloaded load context and report plugins that stay alive.</summary>
    public bool VerifyUnload { get; set; } = true;
}

/// <summary>
/// A plugin currently loaded in the process: the descriptor that discovery produced, plus the two things
/// only the host learns while activating it - the instance and the owning source.
/// <para>
/// The identity reads straight through the descriptor rather than being copied here. Declaring id, name,
/// version, description and location a second time meant the single construction site had to keep them in
/// sync by hand, and the values could drift apart without anything noticing. Carrying the original object
/// also makes a plugin followable from discovery to unload by identity instead of by matching id strings.
/// </para>
/// <para>
/// This is composition and not inheritance for a binding reason: <see cref="PluginDescriptor"/> lives in
/// the plugin-facing contract assembly, whereas <see cref="PluginContext"/> and
/// <see cref="PluginLoadContext"/> are host internals, so one merged type would have to publish them.
/// </para>
/// </summary>
/// <param name="Plugin">The activated instance: the descriptor's own one, or the one created from its assembly.</param>
/// <param name="SourceId">Id of the source that discovered this plugin (null for the built-in assembly scanner).</param>
public sealed record LoadedPlugin(PluginDescriptor Descriptor, IPlugin Plugin, string? SourceId)
{
    /// <summary>Unique plugin id.</summary>
    public string Id => Descriptor.Id;

    /// <summary>Assembly path for assembly-backed plugins, or the origin reported by the source.</summary>
    public string? Location => Descriptor.Location ?? Descriptor.Origin;

    /// <summary>True when the plugin instance was created by another plugin (e.g. a script host).</summary>
    public bool IsInProcess => Descriptor.Instance != null;

    internal PluginContext? Context { get; set; }

    internal PluginLoadContext? LoadContext { get; set; }
}

/// <summary>
/// Owns the plugin set: discovery, loading into collectible load contexts, activation, unload and
/// hot reload. Also owns the shared extension point registry, so a point defined by one plugin can
/// be fed by another - including by a plugin that a third plugin loaded.
/// <para>
/// Nesting is the key property: a plugin that registers an <see cref="IPluginSource"/> gets its
/// discoveries activated as <em>child scopes</em> of its own context. Unloading the loader therefore
/// unloads everything it brought in, in one reverse-order pass, with no separate bookkeeping.
/// </para>
/// </summary>
public sealed class PluginManager : IDisposable
{
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<PendingUnload> _pendingUnload = [];
    private readonly Dictionary<string, SourceRegistration> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processedDescriptors = new(StringComparer.Ordinal);
    private readonly PointRegistry _points = new();
    private readonly Lock _gate = new();

    public PluginManager(IPluginHostAdapter adapter, PluginManagerOptions? options = null)
    {
        Adapter = adapter;
        Options = options ?? new PluginManagerOptions();
        PointRegistry = _points;

        // Host-owned points: survive any single plugin's unload; only contributions are scoped.
        // Pre-defined here so AddFilter/AddSettingsPage (Contribute sugar) never hit "not defined".
        _points.DefinePoint<IVolumeFilter>(FilterPoints.Volume, "Volume filters");
        _points.DefinePoint<SettingsPageContribution>(SettingsPoints.Pages, "设置页面");
    }

    /// <summary>The host adapter this manager drives.</summary>
    public IPluginHostAdapter Adapter { get; }

    /// <summary>Active options.</summary>
    public PluginManagerOptions Options { get; }

    /// <summary>Raised whenever the plugin set changes.</summary>
    public event Action? Changed;

    /// <summary>Shared extension points. Host code can define its own points here before loading.</summary>
    public PointRegistry PointRegistry { get; 
        private set;
    }

    /// <summary>Snapshot of currently loaded plugins, in load order.</summary>
    public IReadOnlyList<LoadedPlugin> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _plugins];
            }
        }
    }

    /// <summary>Ids of currently loaded plugins.</summary>
    public IReadOnlyCollection<string> LoadedPluginIds
    {
        get
        {
            lock (_gate)
            {
                return _plugins.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Id of the source that owns the given plugin, or null for a top-level plugin.</summary>
    public string? GetOwnerPluginId(string pluginId)
    {
        lock (_gate)
        {
            var owners = _sources.Values.Where(s => s.OwnerContext != null).ToList();
            foreach (var source in owners)
            {
                if (source.LoadedIds.Contains(pluginId))
                    return source.OwnerContext!.PluginId;
            }

            return null;
        }
    }

    // -----------------------------------------------------------------------
    //  Loading
    // -----------------------------------------------------------------------

    /// <summary>Scans the configured plugin directories and activates everything valid.</summary>
    public IReadOnlyList<string> LoadAll()
    {
        var loaded = new List<string>();

        // Development builds are scanned first and therefore win: the released copy of the same
        // plugin id is then skipped as a duplicate, so a locally rebuilt plugin shadows the
        // installed one without any configuration change.
        if (Directory.Exists(Options.DevelopmentPluginDirectory))
            loaded.AddRange(LoadDirectory(Options.DevelopmentPluginDirectory));

        if (Directory.Exists(Options.PluginDirectory))
            loaded.AddRange(LoadDirectory(Options.PluginDirectory));

        return loaded;
    }

    /// <summary>
    /// Loads every plugin assembly in a directory. Two layouts are supported: a drop-in <c>*.dll</c> in the
    /// directory itself, and one folder per plugin (<c>&lt;dir&gt;/&lt;plugin&gt;/*.dll</c>), which is what a
    /// plugin with private dependencies needs so its <c>.deps.json</c> and assemblies sit together.
    /// <para>
    /// Files without plugin metadata are skipped silently: a plugin folder also contains the plugin's
    /// private dependencies and their own reference assemblies, and loading every DLL just to find out it
    /// is not a plugin would be slow and unsafe. Only assemblies that declare an identity are considered.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> LoadDirectory(string directory)
    {
        var loaded = new List<string>();
        if (!Directory.Exists(directory))
            return loaded;

        foreach (var file in EnumeratePluginAssemblies(directory))
        {
            var id = TryLoadAssemblyFile(file, sourceId: null, parentContext: null);
            if (id != null)
                loaded.Add(id);
        }

        return loaded;
    }

    private static IEnumerable<string> EnumeratePluginAssemblies(string directory)
    {
        var files = new List<string>();

        files.AddRange(Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly));

        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            files.AddRange(Directory.EnumerateFiles(subdirectory, "*.dll", SearchOption.TopDirectoryOnly));

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>Loads one plugin assembly. Returns the plugin id, or null when it was skipped or failed.</summary>
    public string? LoadAssembly(string assemblyPath) => TryLoadAssemblyFile(assemblyPath, sourceId: null, parentContext: null);

    /// <summary>
    /// Loads a plugin that lives in an already-loaded assembly (the host's own, or a plugin-provided
    /// instance). No load context is created because the code is already resident; unloading it removes
    /// its contributions and previews but cannot release the code itself.
    /// </summary>
    public string? LoadInProcess(
        IPlugin plugin,
        string id,
        string? name = null,
        string? version = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var descriptor = new PluginDescriptor
        {
            Id = id,
            Name = name,
            Version = version,
            Description = description,
            ApiVersion = PluginApi.Version,
            Origin = "in-process",
            Instance = plugin
        };

        return ActivateDescriptor(descriptor, sourceId: null, parentContext: null);
    }

    private string? TryLoadAssemblyFile(string assemblyPath, string? sourceId, PluginContext? parentContext)
    {
        var descriptor = BuildDescriptor(assemblyPath);
        if (descriptor == null)
            return null;

        return ActivateDescriptor(descriptor, sourceId, parentContext);
    }

    /// <summary>
    /// Reads assembly metadata to decide whether a file is a plugin, and validates its API version.
    /// Returns null for anything that declares no plugin identity, which is how private dependencies and
    /// reference assemblies sitting in a plugin folder are ignored without ever being loaded.
    /// </summary>
    private PluginDescriptor? BuildDescriptor(string assemblyPath)
    {
        var metadata = PluginMetadataReader.TryRead(assemblyPath);

        if (metadata == null || string.IsNullOrEmpty(metadata.Id))
        {
            Adapter.Log.Debug($"Skipping '{Path.GetFileName(assemblyPath)}': no plugin metadata.");
            return null;
        }

        if (!IsApiCompatible(metadata.ApiVersion, assemblyPath))
            return null;

        return new PluginDescriptor
        {
            Id = metadata.Id,
            Name = metadata.Name,
            Version = metadata.Version,
            Description = metadata.Description,
            ApiVersion = metadata.ApiVersion,
            Location = assemblyPath,
            Origin = "assembly metadata"
        };
    }

    private bool IsApiCompatible(string? declaredApiVersion, string assemblyPath)
    {
        if (string.IsNullOrEmpty(declaredApiVersion))
        {
            // Metadata may be partial; the load-time [Plugin] check still applies.
            Adapter.Log.Debug($"Plugin '{Path.GetFileName(assemblyPath)}' declares no API version; assuming compatible.");
            return true;
        }

        if (!Options.EnforceApiVersion)
            return true;

        var declared = declaredApiVersion.Split('.')[0];
        var current = PluginApi.Version.Split('.')[0];
        if (declared == current)
            return true;

        Adapter.Log.Warn(
            $"Skipping '{Path.GetFileName(assemblyPath)}': built for plugin API {declaredApiVersion}, " +
            $"host provides {PluginApi.Version}.");
        return false;
    }

    private string? ActivateDescriptor(
        PluginDescriptor descriptor,
        string? sourceId,
        PluginContext? parentContext)
    {
        IPlugin? plugin = null;
        PluginLoadContext? loadContext = null;
        var id = descriptor.Id;

        try
        {
            if (descriptor.Instance != null)
            {
                plugin = descriptor.Instance;
            }
            else
            {
                // The descriptor's location is the assembly to load; one with neither an instance nor a
                // location cannot be activated, and that is a source bug rather than a host one.
                var path = descriptor.Location
                    ?? throw new InvalidOperationException("Descriptor has neither an instance nor an assembly path.");

                loadContext = new PluginLoadContext(path);
                var assembly = loadContext.LoadFromAssemblyPath(path);
                var entry = FindEntryType(assembly);

                if (entry == null)
                {
                    loadContext.Unload();
                    Adapter.Log.Warn($"Plugin '{id}' declares metadata but has no IPlugin entry point in '{Path.GetFileName(path)}'.");
                    return null;
                }

                var attribute = entry.GetCustomAttribute<PluginAttribute>();
                if (id != attribute?.Id)
                {
                    Adapter.Log.Debug(
                        $"Plugin '{id}' metadata and [Plugin] id '{attribute?.Id}' disagree; using the metadata id.");
                }

                plugin = (IPlugin)Activator.CreateInstance(entry)!;
            }

            if (string.IsNullOrEmpty(id))
                return null;

            lock (_gate)
            {
                if (_plugins.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
                {
                    Adapter.Log.Debug($"Plugin '{id}' is already loaded; ignoring duplicate descriptor.");
                    loadContext?.Unload();
                    return null;
                }
            }

            var services = Adapter.Services;
            var context = new PluginContext(id, descriptor.Name, this, services, parentContext);

            try
            {
                plugin.Activate(context);
            }
            catch
            {
                context.Dispose();
                throw;
            }

            var loaded = new LoadedPlugin(descriptor, plugin, sourceId)
            {
                Context = context,
                LoadContext = loadContext
            };

            lock (_gate)
            {
                _plugins.Add(loaded);
            }

            Adapter.Log.Info($"Loaded plugin '{id}'{Describe(loaded)}.");

            // A plugin may have registered loaders while activating; run them now so their plugins
            // become children of this plugin (depth-first, ordered by registration).
            RunPendingSources();

            RaiseChanged();
            return id;
        }
        catch (Exception ex)
        {
            loadContext?.Unload();

            var origin = descriptor.Location ?? descriptor.Origin ?? "(unknown)";
            var subject = string.IsNullOrEmpty(id) ? origin : $"'{id}' from '{origin}'";
            Adapter.Log.Error($"Failed to load plugin {subject}: {ex.Message}", ex);
            return null;
        }
    }

    private static string Describe(LoadedPlugin plugin)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(plugin.Descriptor.Version))
            parts.Add($"v{plugin.Descriptor.Version}");
        if (!string.IsNullOrEmpty(plugin.SourceId))
            parts.Add($"via {plugin.SourceId}");
        if (plugin.IsInProcess)
            parts.Add("in-process");
        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }

    /// <summary>
    /// Finds the plugin entry type: the class marked with <see cref="PluginAttribute"/>, or - for
    /// assemblies built without the attribute - the single concrete <see cref="IPlugin"/>.
    /// </summary>
    private static Type? FindEntryType(Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        Type? fallback = null;
        foreach (var type in types)
        {
            if (type == null || type.IsAbstract || type.IsInterface || !typeof(IPlugin).IsAssignableFrom(type))
                continue;

            if (type.GetCustomAttribute<PluginAttribute>() != null)
                return type;

            fallback ??= type;
        }

        return fallback;
    }

    // -----------------------------------------------------------------------
    //  Plugin sources (plugins acting as plugin loaders)
    // -----------------------------------------------------------------------

    /// <summary>Registers a source. Discoveries from it are activated as children of the owner scope.</summary>
    internal void RegisterSource(IPluginSource source, PluginContext ownerContext)
    {
        lock (_gate)
        {
            if (_sources.ContainsKey(source.Id))
            {
                Adapter.Log.Warn($"Plugin source '{source.Id}' is already registered; ignoring duplicate.");
                return;
            }

            _sources[source.Id] = new SourceRegistration(source, ownerContext);
        }

        Adapter.Log.Debug($"Registered plugin source '{source.Id}' (owner '{ownerContext.PluginId}').");
    }

    internal void RemoveSource(IPluginSource source)
    {
        lock (_gate)
        {
            _sources.Remove(source.Id);
        }
    }

    /// <summary>
    /// Runs every registered source and activates what it finds. Called after each activation and
    /// on demand, so a plugin registered later still sees sources that appeared earlier.
    /// </summary>
    public IReadOnlyList<string> RunPendingSources()
    {
        var loadedNow = new List<string>();
        var guard = 0;

        while (guard++ < 64) // bound the loop: a source that keeps registering sources must not hang the app
        {
            List<(SourceRegistration Registration, PluginDescriptor Descriptor)> work;
            lock (_gate)
            {
                work = [];
                foreach (var registration in _sources.Values)
                {
                    IEnumerable<PluginDescriptor> descriptors;
                    try
                    {
                        descriptors = registration.Source.Discover(BuildSourceContext(registration));
                    }
                    catch (Exception ex)
                    {
                        Adapter.Log.Error($"Plugin source '{registration.Source.Id}' failed to enumerate: {ex.Message}", ex);
                        continue;
                    }

                    foreach (var descriptor in descriptors)
                    {
                        var key = $"{registration.Source.Id}::{descriptor.Id}";
                        if (_processedDescriptors.Add(key))
                            work.Add((registration, descriptor));
                    }
                }
            }

            if (work.Count == 0)
                break;

            foreach (var (registration, descriptor) in work)
            {
                var id = ActivateDescriptor(
                    descriptor,
                    registration.Source.Id,
                    registration.OwnerContext);

                if (id != null)
                {
                    loadedNow.Add(id);
                    lock (_gate)
                    {
                        registration.LoadedIds.Add(id);
                    }
                }
            }
        }

        return loadedNow;
    }

    private IPluginSourceContext BuildSourceContext(SourceRegistration registration) => new SourceContext(
        Options.PluginDirectory,
        Options.DevelopmentPluginDirectory,
        LoadedPluginIds,
        // The source id rides along as a structured property instead of a string prefix glued onto
        // every message, so the window can filter a source without parsing text.
        Adapter.Log.ForContext("Source", registration.Source.Id));

    /// <summary>
    /// Unloads everything a source provided and discovers it again. This is the "reload my format"
    /// operation: a script host calls it after the user edits a script, and because the plugins are
    /// children of the host plugin their contributions disappear and are rebuilt in one pass.
    /// </summary>
    public IReadOnlyList<string> ReloadSource(string sourceId)
    {
        List<string> previous;
        lock (_gate)
        {
            if (!_sources.TryGetValue(sourceId, out var registration))
                return [];

            previous = [.. registration.LoadedIds];
            registration.LoadedIds.Clear();

            foreach (var key in _processedDescriptors
                         .Where(k => k.StartsWith($"{sourceId}::", StringComparison.Ordinal))
                         .ToList())
            {
                _processedDescriptors.Remove(key);
            }
        }

        foreach (var id in previous)
            Unload(id);

        return RunPendingSources();
    }

    /// <summary>Reloads every registered source (used by the "rescan" menu entry).</summary>
    public IReadOnlyList<string> ReloadAllSources()
    {
        List<string> sourceIds;
        lock (_gate)
        {
            sourceIds = [.. _sources.Keys];
        }

        var loaded = new List<string>();
        foreach (var sourceId in sourceIds)
            loaded.AddRange(ReloadSource(sourceId));

        return loaded;
    }

    // -----------------------------------------------------------------------
    //  Unload / reload
    // -----------------------------------------------------------------------

    /// <summary>
    /// Unloads a plugin: disposes its scope (removing every contribution, child scope and preview),
    /// then unloads its load context. Plugins it loaded as a loader are unloaded with it, at any depth,
    /// because their scopes are nested inside its own.
    /// </summary>
    public bool Unload(string pluginId)
    {
        LoadedPlugin root;
        List<LoadedPlugin> doomed;
        List<string> doomedSources;

        lock (_gate)
        {
            var found = _plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));
            if (found == null)
                return false;

            root = found;

            // Everything whose scope nests inside the root's scope goes too - that is the whole
            // contract of a plugin acting as a loader.
            doomed =
            [
                .. _plugins
                    .Where(p => ReferenceEquals(p, root) ||
                                (p.Context != null && root.Context != null && p.Context.IsNestedInside(root.Context)))
            ];

            foreach (var plugin in doomed)
                _plugins.Remove(plugin);

            doomedSources =
            [
                .. _sources
                    .Where(kv => kv.Value.OwnerContext != null &&
                                 doomed.Any(d => ReferenceEquals(d.Context, kv.Value.OwnerContext)))
                    .Select(kv => kv.Key)
            ];
        }

        // Disposing the root scope disposes the nested scopes first, so children are torn down inside out.
        root.Context?.Dispose();

        foreach (var plugin in doomed)
        {
            if (plugin.LoadContext != null)
            {
                plugin.LoadContext.Unload();
                if (Options.VerifyUnload)
                    QueueForVerification(plugin);
            }
        }

        lock (_gate)
        {
            foreach (var sourceId in doomedSources)
                _sources.Remove(sourceId);

            // Forget the source discovery markers for everything that just went away, so a reload (or a
            // later scan) can pick the plugins up again instead of treating them as already processed.
            foreach (var plugin in doomed)
            {
                var stale = _processedDescriptors
                    .Where(key => key.EndsWith($"::{plugin.Id}", StringComparison.Ordinal))
                    .ToList();
                foreach (var key in stale)
                    _processedDescriptors.Remove(key);
            }
        }

        foreach (var plugin in doomed)
            Adapter.Log.Info($"Unloaded plugin '{plugin.Id}'.");

        RaiseChanged();
        return true;
    }

    /// <summary>Unloads and immediately loads the plugin again from the same location (hot reload).</summary>
    public bool Reload(string pluginId)
    {
        LoadedPlugin? plugin;
        lock (_gate)
        {
            plugin = _plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));
        }

        if (plugin == null)
            return false;

        var location = plugin.Location;
        var parent = plugin.Context != null ? FindParentContext(plugin.Context) : null;
        var sourceId = plugin.SourceId;

        if (!Unload(pluginId))
            return false;

        if (location == null || !File.Exists(location) || plugin.IsInProcess)
        {
            // In-process plugins are owned by their loader; re-running the loaders recreates them.
            RunPendingSources();
            return true;
        }

        return TryLoadAssemblyFile(location, sourceId, parent) != null;
    }

    private PluginContext? FindParentContext(PluginContext context)
    {
        lock (_gate)
        {
            foreach (var registration in _sources.Values)
            {
                if (registration.OwnerContext?.Children.Contains(context) == true)
                    return registration.OwnerContext;
            }
        }

        return null;
    }

    private void Teardown(LoadedPlugin plugin)
    {
        plugin.Context?.Dispose();

        if (plugin.LoadContext != null)
        {
            plugin.LoadContext.Unload();
            if (Options.VerifyUnload)
                QueueForVerification(plugin);
        }
    }

    /// <summary>
    /// Records an unloaded context for the leak check. Only the weak reference and the id are kept:
    /// storing the <see cref="LoadedPlugin"/> itself would hold its <see cref="IPlugin"/> instance and
    /// therefore pin the very load context the check is meant to observe.
    /// </summary>
    private void QueueForVerification(LoadedPlugin plugin)
    {
        if (plugin.LoadContext is not { } context)
            return;

        lock (_gate)
        {
            _pendingUnload.Add(new PendingUnload(plugin.Id, new WeakReference(context)));
        }
    }

    private sealed record PendingUnload(string Id, WeakReference ContextReference);

    /// <summary>
    /// Unloads every plugin. Nested plugins are handled by their loader, so this walks the top-level
    /// set; any record left behind (a child whose loader was already gone) is torn down individually.
    /// </summary>
    public void UnloadAll()
    {
        UnloadAllInternal();
        RaiseChanged();
    }

    private void UnloadAllInternal()
    {
        List<LoadedPlugin> roots;
        lock (_gate)
        {
            // Depth: a plugin is a root when it is not nested inside another loaded plugin's scope.
            roots =
            [
                .. _plugins
                    .Where(p => p.Context == null ||
                                !_plugins.Any(other => !ReferenceEquals(other, p) &&
                                                       other.Context != null &&
                                                       p.Context.IsNestedInside(other.Context)))
            ];
        }

        foreach (var plugin in roots)
            Unload(plugin.Id);

        List<LoadedPlugin> leftovers;
        lock (_gate)
        {
            leftovers = [.. _plugins];
            _plugins.Clear();
            _sources.Clear();
            _processedDescriptors.Clear();
        }

        foreach (var plugin in leftovers)
            Teardown(plugin);
    }

    /// <summary>
    /// Runs a full GC and reports plugins whose load context survived, which is the signature of a
    /// leak (a static, an event handler or a cached view model still referencing plugin code).
    /// Called at app shutdown and from tests.
    /// </summary>
    public IReadOnlyList<string> VerifyUnloaded()
    {
        List<PendingUnload> pending;
        lock (_gate)
        {
            pending = [.. _pendingUnload];
        }

        if (pending.Count == 0)
            return [];

        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        var leaked = new List<string>();
        foreach (var entry in pending)
        {
            if (entry.ContextReference.IsAlive)
            {
                leaked.Add(entry.Id);
                Adapter.Log.Warn($"Plugin '{entry.Id}' load context is still alive after unload; it holds a leaked reference.");
            }
            else
            {
                Adapter.Log.Debug($"Plugin '{entry.Id}' load context collected.");
            }
        }

        lock (_gate)
        {
            foreach (var entry in pending)
                _pendingUnload.Remove(entry);
        }

        return leaked;
    }

    internal void ReportUnloadError(string pluginId, Exception ex) =>
        Adapter.Log.Error($"Error while unloading plugin '{pluginId}': {ex.Message}", ex);

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        UnloadAll();
        VerifyUnloaded();
    }

    private sealed class SourceRegistration(IPluginSource source, PluginContext ownerContext)
    {
        public IPluginSource Source { get; } = source;
        public PluginContext OwnerContext { get; } = ownerContext;
        public HashSet<string> LoadedIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SourceContext(
        string pluginDirectory,
        string developmentPluginDirectory,
        IReadOnlyCollection<string> loadedPluginIds,
        IPluginLog log) : IPluginSourceContext
    {
        public string PluginDirectory { get; } = pluginDirectory;
        public string DevelopmentPluginDirectory { get; } = developmentPluginDirectory;
        public IReadOnlyCollection<string> LoadedPluginIds { get; } = loadedPluginIds;
        public IPluginLog Log { get; } = log;
    }
}

