using System.Reflection;
using System.Runtime.CompilerServices;

namespace Layeron.Plugin.Experimental;

public sealed class PluginManagerOptions
{
    /// <summary>Reject descriptors whose declared API major version differs from the host's.</summary>
    public bool EnforceApiVersion { get; set; } = true;

    /// <summary>After an assembly-backed plugin is unloaded, try to collect its load context.</summary>
    public bool VerifyUnload { get; set; } = true;

    /// <summary>
    /// Folder holding plugin-to-plugin contract assemblies. It is loaded into one non-collectible
    /// context and every plugin resolves those names from it, which is what makes a contract type
    /// identical for the publishing and consuming plugins.
    /// </summary>
    public string SharedPluginDirectory { get; set; } = Path.Combine(
        AppContext.BaseDirectory,
        "plugins.shared");
}

/// <summary>
/// Owns discovery results, the dependency graph, plugin activation and the host lifecycle.
/// <para>
/// The manager has one rule that explains most of its behaviour: a plugin is only ever activated
/// after every dependency is already loaded. Ordering, cascade unload, reload and application
/// shutdown all fall out of that invariant rather than being separate special cases.
/// </para>
/// </summary>
public sealed class PluginManager : IDisposable
{
    private readonly Dictionary<string, PluginDescriptor> _candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginRuntime> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceRegistration> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processedDescriptors = new(StringComparer.Ordinal);
    private readonly List<PendingUnload> _pendingUnload = [];
    private readonly Lock _gate = new();
    private readonly PluginSharedAssemblies _shared;

    private long _sequence;
    private bool _initialized;
    private bool _shutdown;

    public PluginManager(IPluginHostServices services, PluginManagerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        Options = options ?? new PluginManagerOptions();
        _shared = new PluginSharedAssemblies(
            Options.SharedPluginDirectory,
            message => Services.Log.Warn(message));
    }

    public IPluginHostServices Services { get; }

    public PluginManagerOptions Options { get; }

    /// <summary>Extension points visible to every plugin.</summary>
    public PointRegistry Points { get; } = new();

    /// <summary>Raised when the active plugin set changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Descriptors of the active plugins, in activation order. They are the same type used for
    /// discovery, completed by the manager so host-discovered plugins and a loader's children expose
    /// the same fields. <see cref="PluginDescriptor.SourceId"/> tells them apart.
    /// </summary>
    public IReadOnlyList<PluginDescriptor> Plugins
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _active.Values
                        .OrderBy(plugin => plugin.ActivationOrder)
                        .Select(plugin => plugin.Descriptor)
                ];
            }
        }
    }

    public IReadOnlyCollection<string> LoadedIds
    {
        get
        {
            lock (_gate)
            {
                return _active.Keys.ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    public bool IsLoaded(string pluginId)
    {
        lock (_gate)
        {
            return _active.ContainsKey(pluginId);
        }
    }

    /// <summary>Simple names of the contract assemblies currently deployed in the shared context.</summary>
    public IReadOnlyCollection<string> SharedContractNames => _shared.Names;

    public ExtensionPoint<T>? GetPoint<T>(string? key = null) => Points.GetPoint<T>(key);

    /// <summary>Non-generic lookup for host code that must not reference a plugin contract.</summary>
    public IExtensionPoint? GetPoint(string id) => Points.GetPoint(id);

    // -----------------------------------------------------------------------
    //  Discovery
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds a candidate. Nothing is activated until <see cref="LoadAll"/>; adding the whole
    /// discovery result first is what lets the resolver see the complete dependency graph.
    /// </summary>
    public bool Add(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.ApiVersion);
        ThrowIfShutdown();

        lock (_gate)
        {
            if (_active.ContainsKey(descriptor.Id) ||
                !_candidates.TryAdd(descriptor.Id, descriptor.WithDefaults(null)))
            {
                Services.Log.Debug($"Plugin '{descriptor.Id}' is already loaded or pending; ignoring duplicate.");
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads one assembly's metadata and adds it as a candidate.</summary>
    public PluginDescriptor? AddAssembly(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ThrowIfShutdown();

        var metadata = PluginMetadataReader.TryRead(assemblyPath);
        if (metadata is null)
        {
            Services.Log.Debug($"Skipping '{Path.GetFileName(assemblyPath)}': no plugin metadata.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(metadata.ApiVersion))
        {
            Services.Log.Warn(
                $"Skipping '{Path.GetFileName(assemblyPath)}': " +
                $"no {PluginMetadataKeys.ApiVersion} metadata.");
            return null;
        }

        if (!IsAssemblyApiCompatible(metadata.ApiVersion, assemblyPath))
            return null;

        var descriptor = new PluginDescriptor
        {
            Id = metadata.Id,
            Name = metadata.Name,
            Version = metadata.Version,
            Description = metadata.Description,
            ApiVersion = metadata.ApiVersion,
            Lifetime = metadata.Lifetime,
            Dependencies = metadata.Dependencies,
            AssemblyPath = assemblyPath
        };

        return Add(descriptor) ? descriptor.WithDefaults(null) : null;
    }

    /// <summary>Adds every assembly with plugin metadata from a directory, then loads everything.</summary>
    public LoadReport LoadDirectory(string directory)
    {
        ThrowIfShutdown();

        if (!Directory.Exists(directory))
            return new LoadReport([], []);

        foreach (var file in EnumeratePluginAssemblies(directory))
            AddAssembly(file);

        return LoadAll();
    }

    /// <summary>Adds one assembly and loads it immediately.</summary>
    public bool LoadAssembly(string assemblyPath) =>
        AddAssembly(assemblyPath) is not null && LoadAll().Success;

    // -----------------------------------------------------------------------
    //  Plugin sources (application plugins acting as loaders)
    // -----------------------------------------------------------------------

    /// <summary>Ids of the currently registered sources.</summary>
    public IReadOnlyCollection<string> SourceIds
    {
        get
        {
            lock (_gate)
            {
                return _sources.Keys.ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    internal void RegisterSource(IPluginSource source, PluginContext owner)
    {
        ThrowIfShutdown();

        if (owner.Descriptor.Lifetime != PluginLifetime.Application)
        {
            throw new InvalidOperationException(
                "Only application-lifetime plugins may register a plugin source.");
        }

        if (string.IsNullOrWhiteSpace(source.Id))
            throw new InvalidOperationException("A plugin source must have an id.");

        // "host" is the pseudo-source every host-discovered descriptor reports. Allowing a real
        // source to take it would make source reload and host discovery indistinguishable.
        if (string.Equals(source.Id, PluginDescriptor.HostSourceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Plugin source id '{PluginDescriptor.HostSourceId}' is reserved for host discovery.");
        }

        lock (_gate)
        {
            if (!_sources.TryAdd(source.Id, new SourceRegistration(source, owner)))
            {
                throw new InvalidOperationException(
                    $"Plugin source '{source.Id}' is already registered.");
            }
        }

        Services.Log.Debug($"Registered plugin source '{source.Id}' owned by '{owner.PluginId}'.");
    }

    internal void RemoveSource(IPluginSource source)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(source.Id, out var registration) ||
                !ReferenceEquals(registration.Source, source))
            {
                return;
            }

            _sources.Remove(source.Id);

            foreach (var id in _candidates
                         .Where(pair => pair.Value.SourceId == source.Id)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _candidates.Remove(id);
            }

            foreach (var key in _processedDescriptors
                         .Where(key => key.StartsWith($"{source.Id}::", StringComparison.Ordinal))
                         .ToList())
            {
                _processedDescriptors.Remove(key);
            }
        }
    }

    /// <summary>
    /// Unloads everything a source currently provides (plus any dependents outside the source),
    /// then discovers and loads it again. This is the loader's "rescan" operation.
    /// </summary>
    public LoadReport ReloadSource(string sourceId)
    {
        ThrowIfShutdown();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        List<PluginRuntime> doomed;

        lock (_gate)
        {
            if (!_sources.ContainsKey(sourceId))
            {
                return new LoadReport(
                    [],
                    [new PluginFailure(sourceId, $"Plugin source '{sourceId}' is not registered.")]);
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var runtime in _active.Values.Where(plugin => plugin.Descriptor.SourceId == sourceId))
            {
                foreach (var dependent in DependentsClosure(runtime.Id))
                    ids.Add(dependent.Id);
            }

            doomed = [.. ids
                .Select(id => _active[id])
                .OrderByDescending(plugin => plugin.ActivationOrder)];

            if (doomed.FirstOrDefault(plugin => plugin.Lifetime == PluginLifetime.Application) is { } blocked)
            {
                return new LoadReport(
                    [],
                    [new PluginFailure(
                        sourceId,
                        $"Cannot reload source '{sourceId}': application-lifetime plugin '{blocked.Id}' " +
                        "is the target or depends on it.")]);
            }

            // A reload is a fresh discovery: pending candidates and processed markers from the
            // previous pass must not suppress the source's output.
            foreach (var id in _candidates
                         .Where(pair => pair.Value.SourceId == sourceId)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _candidates.Remove(id);
            }

            foreach (var key in _processedDescriptors
                         .Where(key => key.StartsWith($"{sourceId}::", StringComparison.Ordinal))
                         .ToList())
            {
                _processedDescriptors.Remove(key);
            }
        }

        UnloadPlugins(doomed, $"reload of source '{sourceId}'");

        var report = LoadAll();
        Services.Log.Info(
            $"Reloaded source '{sourceId}': {report.Loaded.Count} plugin(s) activated, " +
            $"{report.Failed.Count} failure(s).");
        return report;
    }

    /// <summary>Reloads every registered source.</summary>
    public LoadReport ReloadAllSources()
    {
        var loaded = new List<string>();
        var failed = new List<PluginFailure>();

        foreach (var sourceId in SourceIds)
        {
            var report = ReloadSource(sourceId);
            loaded.AddRange(report.Loaded);
            failed.AddRange(report.Failed);
        }

        return new LoadReport(loaded, failed);
    }

    /// <summary>
    /// Discovers from every source until no new ids appear. Sources are evaluated after the
    /// application phase, so their children share the host's dependency graph and ordering.
    /// </summary>
    private void RunPendingSources(List<PluginFailure> failures)
    {
        for (var pass = 0; pass < 16; pass++)
        {
            List<SourceRegistration> registrations;
            lock (_gate)
            {
                registrations = [.. _sources.Values];
            }

            if (registrations.Count == 0)
                return;

            var discovered = new List<(SourceRegistration Registration, PluginDescriptor Descriptor)>();
            foreach (var registration in registrations)
            {
                IEnumerable<PluginDescriptor> descriptors;
                try
                {
                    descriptors = registration.Source.Discover(BuildSourceContext());
                }
                catch (Exception ex)
                {
                    Services.Log.Error(
                        $"Plugin source '{registration.Source.Id}' failed to enumerate: {ex.Message}",
                        ex);
                    continue;
                }

                foreach (var descriptor in descriptors)
                {
                    if (string.IsNullOrWhiteSpace(descriptor.Id))
                    {
                        failures.Add(new PluginFailure(
                            registration.Source.Id,
                            "A source returned a descriptor without an id."));
                        continue;
                    }

                    var key = $"{registration.Source.Id}::{descriptor.Id}";
                    lock (_gate)
                    {
                        if (!_processedDescriptors.Add(key))
                            continue;
                    }

                    discovered.Add((registration, descriptor));
                }
            }

            if (discovered.Count == 0)
                return;

            foreach (var (registration, descriptor) in discovered)
            {
                if (string.IsNullOrWhiteSpace(descriptor.ApiVersion))
                {
                    const string reason = "A plugin descriptor must declare its ApiVersion.";
                    Services.Log.Warn($"Rejected plugin '{descriptor.Id}': {reason}");
                    failures.Add(new PluginFailure(descriptor.Id, reason));
                    continue;
                }

                if (descriptor.Lifetime == PluginLifetime.Application)
                {
                    var reason =
                        $"Source '{registration.Source.Id}' may only discover reloadable plugins; " +
                        "application-lifetime plugins must be in the initial candidate set.";
                    Services.Log.Warn($"Rejected plugin '{descriptor.Id}': {reason}");
                    failures.Add(new PluginFailure(descriptor.Id, reason));
                    continue;
                }

                lock (_gate)
                {
                    if (_active.ContainsKey(descriptor.Id))
                    {
                        Services.Log.Debug(
                            $"Source '{registration.Source.Id}' returned already active plugin " +
                            $"'{descriptor.Id}'; ignoring.");
                        continue;
                    }

                    _candidates.TryAdd(
                        descriptor.Id,
                        descriptor.WithDefaults(registration.Source.Id));
                }
            }
        }
    }

    private IPluginSourceContext BuildSourceContext()
    {
        IReadOnlyCollection<string> loadedIds;
        lock (_gate)
        {
            loadedIds = [.. _active.Keys];
        }

        return new SourceContext(
            Options.SharedPluginDirectory,
            loadedIds,
            Services.Log);
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

    private bool IsAssemblyApiCompatible(string declaredApiVersion, string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(declaredApiVersion) || !Options.EnforceApiVersion)
            return true;

        var declared = declaredApiVersion.Split('.')[0];
        var current = PluginApi.Version.Split('.')[0];
        if (string.Equals(declared, current, StringComparison.Ordinal))
            return true;

        Services.Log.Warn(
            $"Skipping '{Path.GetFileName(assemblyPath)}': built for plugin API {declaredApiVersion}, " +
            $"host provides {PluginApi.Version}.");
        return false;
    }

    // -----------------------------------------------------------------------
    //  Loading
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads every pending candidate in dependency order.
    /// <para>
    /// On the first call, application-lifetime candidates are loaded and enabled before any
    /// reloadable plugin is activated. Later calls only consider reloadable candidates, because an
    /// application plugin added after startup would not have been part of the host's own startup
    /// sequence.
    /// </para>
    /// </summary>
    public LoadReport LoadAll()
    {
        ThrowIfShutdown();

        var loaded = new List<string>();
        var failures = new List<PluginFailure>();

        List<PluginDescriptor> candidates;
        lock (_gate)
        {
            candidates = [.. _candidates.Values];
        }

        var application = candidates
            .Where(candidate =>
                candidate.Lifetime == PluginLifetime.Application &&
                !IsLoaded(candidate.Id))
            .ToList();

        if (!_initialized)
        {
            LoadApplicationPlugins(application, candidates, loaded, failures);
            lock (_gate)
            {
                _initialized = true;
            }
        }
        else
        {
            foreach (var candidate in application)
            {
                failures.Add(new PluginFailure(
                    candidate.Id,
                    "Application-lifetime plugins must be added before the first load."));
            }
        }

        // Sources registered by application plugins during load are evaluated before any
        // reloadable plugin, so their children and the host's candidates share one graph.
        RunPendingSources(failures);

        lock (_gate)
        {
            candidates = [.. _candidates.Values];
        }

        var regular = candidates
            .Where(candidate =>
                candidate.Lifetime == PluginLifetime.Reloadable &&
                !IsLoaded(candidate.Id))
            .ToList();

        LoadReloadablePlugins(regular, loaded, failures);

        if (loaded.Count > 0)
            RaiseChanged();

        return new LoadReport(loaded, failures);
    }

    private void LoadApplicationPlugins(
        IReadOnlyList<PluginDescriptor> candidates,
        IReadOnlyList<PluginDescriptor> allCandidates,
        List<string> loaded,
        List<PluginFailure> failures)
    {
        if (candidates.Count == 0)
            return;

        var byId = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);

        // Application plugins are planned against the full candidate set so a dependency on a
        // reloadable plugin is reported as the lifetime-rule violation it is, not as "missing".
        var plan = DependencyResolver.Resolve(
            candidates,
            SnapshotAvailable(allCandidates),
            Options.EnforceApiVersion);

        failures.AddRange(plan.Failures);

        var enabled = new List<PluginRuntime>();
        foreach (var descriptor in plan.Order)
        {
            if (CheckDependencies(descriptor) is { } problem)
            {
                failures.Add(new PluginFailure(descriptor.Id, problem));
                continue;
            }

            var plugin = Activate(byId[descriptor.Id], application: true, out var error);
            if (plugin is null)
            {
                failures.Add(new PluginFailure(descriptor.Id, error ?? "Activation failed."));
                continue;
            }

            loaded.Add(plugin.Id);
            enabled.Add(plugin);
        }

        // OnEnable runs only after every application plugin has been loaded and activated.
        foreach (var plugin in enabled)
        {
            // A previous OnEnable failure rolls back that plugin and its dependents. Skip them.
            if (!IsLoaded(plugin.Id))
                continue;

            try
            {
                ((IApplicationPlugin)plugin.Instance).OnEnable(plugin.Context);
            }
            catch (Exception ex)
            {
                Services.Log.Error($"Plugin '{plugin.Id}' failed during OnEnable: {ex.Message}", ex);
                failures.Add(new PluginFailure(plugin.Id, $"OnEnable failed: {ex.Message}"));
                RollbackApplicationPlugin(plugin, loaded);
            }
        }
    }

    private void LoadReloadablePlugins(
        IReadOnlyList<PluginDescriptor> candidates,
        List<string> loaded,
        List<PluginFailure> failures)
    {
        if (candidates.Count == 0)
            return;

        var byId = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);

        var plan = DependencyResolver.Resolve(
            candidates,
            SnapshotAvailable(candidates),
            Options.EnforceApiVersion);

        failures.AddRange(plan.Failures);

        foreach (var descriptor in plan.Order)
        {
            if (CheckDependencies(descriptor) is { } problem)
            {
                failures.Add(new PluginFailure(descriptor.Id, problem));
                continue;
            }

            var plugin = Activate(byId[descriptor.Id], application: false, out var error);
            if (plugin is null)
            {
                failures.Add(new PluginFailure(descriptor.Id, error ?? "Activation failed."));
                continue;
            }

            loaded.Add(plugin.Id);
        }
    }

    private IReadOnlyDictionary<string, PluginDescriptor> SnapshotAvailable(
        IEnumerable<PluginDescriptor> candidates)
    {
        var available = new Dictionary<string, PluginDescriptor>(StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var plugin in _active.Values)
                available[plugin.Id] = plugin.Descriptor;
        }

        foreach (var candidate in candidates)
            available[candidate.Id] = candidate;

        return available;
    }

    private string? CheckDependencies(PluginDescriptor descriptor)
    {
        lock (_gate)
        {
            foreach (var dependency in descriptor.Dependencies)
            {
                if (!_active.TryGetValue(dependency.Id, out var provider))
                    return $"Dependency '{dependency.Id}' is not loaded; it failed or was unloaded.";

                if (descriptor.Lifetime == PluginLifetime.Application &&
                    provider.Lifetime != PluginLifetime.Application)
                {
                    return
                        $"Application-lifetime plugin '{descriptor.Id}' cannot depend on " +
                        $"reloadable plugin '{dependency.Id}'.";
                }

                if (DependencyResolver.VersionProblem(provider.Descriptor, dependency) is { } problem)
                    return problem;
            }
        }

        return null;
    }

    private PluginRuntime? Activate(PluginDescriptor descriptor, bool application, out string? error)
    {
        error = null;
        PluginContext? context = null;
        PluginLoadContext? loadContext = null;

        try
        {
            lock (_gate)
            {
                if (_active.ContainsKey(descriptor.Id))
                    throw new InvalidOperationException($"Plugin '{descriptor.Id}' is already loaded.");
            }

            var instance = CreateInstance(descriptor, ref loadContext);

            if (application && instance is not IApplicationPlugin)
            {
                throw new InvalidOperationException(
                    "An application-lifetime plugin must implement IApplicationPlugin.");
            }

            context = new PluginContext(descriptor, this, Services);

            if (application)
                ((IApplicationPlugin)instance).OnLoad(context);

            instance.Activate(context);

            var loaded = new PluginRuntime(
                descriptor,
                instance,
                context,
                loadContext,
                Interlocked.Increment(ref _sequence));

            lock (_gate)
            {
                if (!_active.TryAdd(descriptor.Id, loaded))
                    throw new InvalidOperationException($"Plugin '{descriptor.Id}' is already loaded.");

                _candidates.Remove(descriptor.Id);
            }

            var via = descriptor.FromLoader
                ? $" via source '{descriptor.SourceId}'"
                : string.Empty;
            Services.Log.Info($"Loaded plugin '{descriptor.Id}' ({descriptor.Lifetime}){via}.");
            return loaded;
        }
        catch (Exception ex)
        {
            context?.Dispose();
            loadContext?.Unload();
            error = ex.Message;
            Services.Log.Error($"Failed to load plugin '{descriptor.Id}': {ex.Message}", ex);
            return null;
        }
    }

    private IPlugin CreateInstance(PluginDescriptor descriptor, ref PluginLoadContext? loadContext)
    {
        if (descriptor.Factory is { } factory)
            return factory() ?? throw new InvalidOperationException("The plugin factory returned null.");

        var path = descriptor.AssemblyPath
            ?? throw new InvalidOperationException("Descriptor has neither a factory nor an assembly path.");

        loadContext = new PluginLoadContext(path, _shared);
        var assembly = loadContext.LoadFromAssemblyPath(path);
        var entry = FindEntryType(assembly)
            ?? throw new InvalidOperationException(
                $"No IPlugin entry type in '{Path.GetFileName(path)}'.");

        return (IPlugin)Activator.CreateInstance(entry)!;
    }

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
        catch (Exception ex) when (IsMissingDependency(ex))
        {
            return null;
        }

        Type? fallback = null;
        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface)
                continue;

            try
            {
                if (!typeof(IPlugin).IsAssignableFrom(type))
                    continue;

                if (type.GetCustomAttribute<PluginAttribute>() is not null)
                    return type;

                fallback ??= type;
            }
            catch (Exception ex) when (IsMissingDependency(ex))
            {
                // The type cannot implement IPlugin in this host if its dependencies are missing.
            }
        }

        return fallback;
    }

    private static bool IsMissingDependency(Exception ex) =>
        ex is FileNotFoundException or FileLoadException or TypeLoadException
            or BadImageFormatException or NotSupportedException;

    // -----------------------------------------------------------------------
    //  Unload / reload / shutdown
    // -----------------------------------------------------------------------

    /// <summary>
    /// Unloads a plugin and every loaded plugin that depends on it. The unload is refused if the
    /// target or any dependent is application-lifetime. Dependents are disposed before their
    /// dependencies because the closure is ordered by reverse activation order.
    /// </summary>
    public bool Unload(string pluginId)
    {
        ThrowIfShutdown();

        List<PluginRuntime> doomed;
        lock (_gate)
        {
            if (!_active.ContainsKey(pluginId))
                return false;

            doomed = DependentsClosure(pluginId);

            if (doomed.FirstOrDefault(plugin => plugin.Lifetime == PluginLifetime.Application) is { } blocked)
            {
                Services.Log.Warn(
                    $"Cannot unload '{pluginId}': application-lifetime plugin '{blocked.Id}' " +
                    "is the target or depends on it.");
                return false;
            }
        }

        if (doomed.Count > 1)
        {
            Services.Log.Info(
                $"Unloading '{pluginId}' also unloads {doomed.Count - 1} dependent plugin(s).");
        }

        UnloadPlugins(doomed, $"unload of '{pluginId}'");
        return true;
    }

    /// <summary>
    /// Hot reloads a plugin and its reloadable dependents. The whole dependent subtree is unloaded
    /// first and then activated again from its descriptors, so no plugin ever observes a stale
    /// dependency instance.
    /// </summary>
    public bool Reload(string pluginId)
    {
        ThrowIfShutdown();

        List<PluginRuntime> doomed;
        List<PluginDescriptor> candidates;

        lock (_gate)
        {
            if (!_active.TryGetValue(pluginId, out var target))
                return false;

            if (target.Lifetime == PluginLifetime.Application)
            {
                Services.Log.Warn($"Plugin '{pluginId}' is application-lifetime and cannot be reloaded.");
                return false;
            }

            doomed = DependentsClosure(pluginId);
            if (doomed.FirstOrDefault(plugin => plugin.Lifetime == PluginLifetime.Application) is { } blocked)
            {
                Services.Log.Warn(
                    $"Cannot reload '{pluginId}': application-lifetime plugin '{blocked.Id}' " +
                    "is the target or depends on it.");
                return false;
            }

            // Ascending activation order is dependency-first; activation uses this order.
            candidates = [.. doomed
                .OrderBy(plugin => plugin.ActivationOrder)
                .Select(plugin => plugin.Descriptor)];
        }

        UnloadPlugins(doomed, $"reload of '{pluginId}'");

        var loaded = new List<string>();
        var failures = new List<PluginFailure>();
        LoadReloadablePlugins(candidates, loaded, failures);

        foreach (var failure in failures)
            Services.Log.Warn($"Reload of '{failure.PluginId}' failed: {failure.Reason}");

        if (!loaded.Contains(pluginId))
        {
            Services.Log.Warn($"Reload of '{pluginId}' failed; it remains pending for a later LoadAll.");
        }

        // Keep every descriptor that could not be reactivated, so a later LoadAll can retry after
        // the underlying problem (a missing assembly, for example) has been fixed.
        lock (_gate)
        {
            foreach (var candidate in candidates)
            {
                if (!_active.ContainsKey(candidate.Id))
                    _candidates.TryAdd(candidate.Id, candidate);
            }
        }

        if (loaded.Count > 0)
            RaiseChanged();

        return loaded.Contains(pluginId);
    }

    /// <summary>
    /// Unloads all reloadable plugins, then runs application shutdown callbacks in reverse
    /// dependency order and disposes their contexts. Idempotent.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Shutdown()
    {
        List<PluginRuntime> regular;
        List<PluginRuntime> application;

        lock (_gate)
        {
            if (_shutdown)
                return;

            _shutdown = true;
            regular = [.. _active.Values
                .Where(plugin => plugin.Lifetime == PluginLifetime.Reloadable)
                .OrderByDescending(plugin => plugin.ActivationOrder)];

            application = [.. _active.Values
                .Where(plugin => plugin.Lifetime == PluginLifetime.Application)
                .OrderByDescending(plugin => plugin.ActivationOrder)];

            _candidates.Clear();
            _sources.Clear();
            _processedDescriptors.Clear();
        }

        UnloadPlugins(regular, "shutdown");

        foreach (var plugin in application)
        {
            try
            {
                ((IApplicationPlugin)plugin.Instance).OnHostShutdown(plugin.Context);
            }
            catch (Exception ex)
            {
                Services.Log.Error(
                    $"Plugin '{plugin.Id}' failed during OnHostShutdown: {ex.Message}",
                    ex);
            }
        }

        if (application.Count > 0)
        {
            lock (_gate)
            {
                foreach (var plugin in application)
                    _active.Remove(plugin.Id);
            }

            foreach (var plugin in application)
                TearDown(plugin);

            foreach (var plugin in application)
                Services.Log.Info($"Unloaded plugin '{plugin.Id}' (shutdown).");

            RaiseChanged();
        }
    }

    /// <summary>Ordered dependents-first: the target and every loaded plugin that transitively depends on it.</summary>
    private List<PluginRuntime> DependentsClosure(string pluginId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { pluginId };
        bool added;

        do
        {
            added = false;

            foreach (var plugin in _active.Values)
            {
                if (plugin.Descriptor.Dependencies.Any(dependency => ids.Contains(dependency.Id)) &&
                    ids.Add(plugin.Id))
                {
                    added = true;
                }
            }
        }
        while (added);

        return [.. _active.Values
            .Where(plugin => ids.Contains(plugin.Id))
            .OrderByDescending(plugin => plugin.ActivationOrder)];
    }

    private void UnloadPlugins(IReadOnlyList<PluginRuntime> plugins, string reason)
    {
        if (plugins.Count == 0)
            return;

        lock (_gate)
        {
            foreach (var plugin in plugins)
                _active.Remove(plugin.Id);
        }

        foreach (var plugin in plugins)
            TearDown(plugin);

        foreach (var plugin in plugins)
            Services.Log.Info($"Unloaded plugin '{plugin.Id}' ({reason}).");

        RaiseChanged();
    }

    private void RollbackApplicationPlugin(PluginRuntime failed, List<string> loaded)
    {
        List<PluginRuntime> doomed;

        lock (_gate)
        {
            var ids = DependentsClosure(failed.Id)
                .Where(plugin => plugin.Lifetime == PluginLifetime.Application)
                .Select(plugin => plugin.Id)
                .ToHashSet(StringComparer.Ordinal);

            doomed = [.. _active.Values
                .Where(plugin => ids.Contains(plugin.Id))
                .OrderByDescending(plugin => plugin.ActivationOrder)];

            foreach (var plugin in doomed)
                _active.Remove(plugin.Id);
        }

        foreach (var plugin in doomed)
        {
            loaded.Remove(plugin.Id);
            TearDown(plugin);
            Services.Log.Warn($"Rolled back application plugin '{plugin.Id}' after an enable failure.");
        }

        RaiseChanged();
    }

    private void TearDown(PluginRuntime plugin)
    {
        plugin.Context.Dispose();

        if (plugin.LoadContext is null)
            return;

        plugin.LoadContext.Unload();

        if (Options.VerifyUnload)
        {
            lock (_gate)
            {
                _pendingUnload.Add(new PendingUnload(plugin.Id, new WeakReference(plugin.LoadContext)));
            }
        }
    }

    private sealed record PendingUnload(string Id, WeakReference ContextReference);

    /// <summary>
    /// Collects unloaded load contexts and returns the ids that survived, which is the signature of
    /// a plugin still referenced by a static, event subscription or cached view.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IReadOnlyList<string> VerifyUnloaded()
    {
        List<PendingUnload> pending;

        lock (_gate)
        {
            pending = [.. _pendingUnload];
            _pendingUnload.Clear();
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
                Services.Log.Warn(
                    $"Plugin '{entry.Id}' load context is still alive after unload; it holds a leaked reference.");
            }
            else
            {
                Services.Log.Debug($"Plugin '{entry.Id}' load context collected.");
            }
        }

        return leaked;
    }

    internal void ReportUnloadError(string pluginId, Exception exception) =>
        Services.Log.Error($"Error while unloading plugin '{pluginId}': {exception.Message}", exception);

    private void RaiseChanged() => Changed?.Invoke();

    private void ThrowIfShutdown()
    {
        if (_shutdown)
            throw new InvalidOperationException("The plugin manager has been shut down.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Dispose()
    {
        Shutdown();

        if (Options.VerifyUnload)
            VerifyUnloaded();
    }

    private sealed class SourceRegistration(IPluginSource source, PluginContext owner)
    {
        public IPluginSource Source { get; } = source;

        public PluginContext Owner { get; } = owner;
    }

    private sealed class SourceContext(
        string sharedContractDirectory,
        IReadOnlyCollection<string> loadedPluginIds,
        IPluginLog log) : IPluginSourceContext
    {
        public string SharedContractDirectory { get; } = sharedContractDirectory;

        public IReadOnlyCollection<string> LoadedPluginIds { get; } = loadedPluginIds;

        public IPluginLog Log { get; } = log;
    }
}

