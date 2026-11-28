namespace Layeron.Plugin.Experimental;

internal static class Scenarios
{
    /// <summary>
    /// End-to-end assembly scenario: three real plugin assemblies form a dependency chain, the
    /// first is application-lifetime, and unloading/reloading exercises the cascade.
    /// </summary>
    public static void AssemblyDependencyChain()
    {
        EnsureSamplePluginsExist(
            SamplePaths.ApplicationPlugin,
            SamplePaths.FeaturePlugin,
            SamplePaths.TopPlugin,
            SamplePaths.SharedContract);

        var host = new ScenarioHost();
        using var manager = new PluginManager(host);

        Check.NotNull(manager.AddAssembly(SamplePaths.ApplicationPlugin), "app plugin assembly did not declare metadata.");
        Check.NotNull(manager.AddAssembly(SamplePaths.FeaturePlugin), "feature plugin assembly did not declare metadata.");
        Check.NotNull(manager.AddAssembly(SamplePaths.TopPlugin), "top plugin assembly did not declare metadata.");

        var report = manager.LoadAll();
        Check.True(report.Success, Describe(report));

        Check.Sequence(manager.LoadedIds.OrderBy(id => id, StringComparer.Ordinal), "sample.app", "sample.feature", "sample.top");

        var events = host.PluginEvents;
        Check.True(Check.IndexOf(events, "sample.app:OnLoad") >= 0, "application plugin OnLoad did not run.");
        Check.True(
            Check.IndexOf(events, "sample.app:Activate") < Check.IndexOf(events, "sample.app:OnEnable"),
            "Activate must run before OnEnable.");
        Check.True(
            Check.IndexOf(events, "sample.app:OnEnable") < Check.IndexOf(events, "sample.feature:Activate"),
            "all application plugins must be enabled before reloadable plugins activate.");
        Check.True(
            Check.IndexOf(events, "sample.feature:Activate") < Check.IndexOf(events, "sample.top:Activate"),
            "dependencies must activate before dependents.");

        var point = manager.GetPoint("experimental.sample.observers");
        Check.NotNull(point, "the application plugin must publish the observer point.");
        Check.Equal("experimental.sample.observers", point!.Id, "the point id must come from the contract attribute.");
        Check.Equal("ISampleObserver", point.ContributionType.Name, "the point must use the shared contract type.");
        Check.Equal(
            "Layeron.Plugin.Experimental.Sample.Actions.Api",
            point.ContributionType.Assembly.GetName().Name,
            "the point contract must come from the plugin-published API assembly.");
        Check.True(
            point.ContributionType.Assembly.Location.Contains("plugins.shared", StringComparison.OrdinalIgnoreCase),
            "the point contract must be loaded from the shared contract directory.");
        Check.True(
            manager.SharedContractNames.Contains("Layeron.Plugin.Experimental.Sample.Actions.Api"),
            "the shared contract loader must own the contract assembly.");
        Check.Equal(2, point.Count, "feature and top must both observe the shared point.");

        // The app called feature/top and they called back through reply. The hub recorded messages
        // instead of just aggregating static items.
        var broadcasts = Check.CountStartingWith(host.PluginEvents, "sample.app:message:");
        Check.True(broadcasts >= 5, $"expected a growing transcript, saw {broadcasts} message(s).");
        Check.True(
            host.PluginEvents.Any(item =>
                item.StartsWith("sample.feature:receive:", StringComparison.Ordinal) &&
                item.Contains("sample.app:welcome", StringComparison.Ordinal)),
            "feature never received a message from the application hub.");
        Check.True(
            host.PluginEvents.Any(item =>
                item.StartsWith("sample.top:receive:", StringComparison.Ordinal) &&
                item.Contains("sample.app:welcome", StringComparison.Ordinal)),
            "top never received a message from the application hub.");
        Check.True(
            host.PluginEvents.Any(item => item.Contains(":sample.feature:feature-ack", StringComparison.Ordinal)),
            "the hub never recorded feature's reply.");
        Check.True(
            host.PluginEvents.Any(item => item.Contains(":sample.top:top-ack", StringComparison.Ordinal)),
            "the hub never recorded top's reply.");

        Check.False(manager.Unload("sample.app"), "an application-lifetime plugin must not unload.");
        Check.False(manager.Reload("sample.app"), "an application-lifetime plugin must not reload.");

        // A regular plugin that observes its own context disposal. Shutdown must tear it down
        // before the application plugin's OnHostShutdown runs.
        manager.Add(new PluginDescriptor
        {
            Id = "sample.tracker",
            Name = "Sample Tracker",
            Version = "1.0.0",
            ApiVersion = PluginApi.Version,
            Dependencies = [new PluginDependency("sample.app")],
            Factory = context => new TrackingPlugin(host, "sample.tracker", context)
        });

        Check.True(manager.LoadAll().Success, "the tracker plugin should load after the application plugin.");
        Check.True(manager.IsLoaded("sample.tracker"), "the tracker plugin is missing.");

        // Unloading feature must also unload top, but leave app and tracker alone.
        Check.True(manager.Unload("sample.feature"), "feature should unload.");
        Check.False(manager.IsLoaded("sample.feature"), "feature is still loaded.");
        Check.False(manager.IsLoaded("sample.top"), "top should have been unloaded as a dependent.");
        Check.True(manager.IsLoaded("sample.app"), "the application plugin must survive.");
        Check.True(manager.IsLoaded("sample.tracker"), "unrelated plugins must survive.");

        Check.Equal(0, point.Count, "both observers must be removed with their plugins.");
        Check.True(
            Check.IndexOf(host.PluginEvents, "sample.top:Dispose") < Check.IndexOf(host.PluginEvents, "sample.feature:Dispose"),
            "dependents must be disposed before their dependency.");

        // Reload brings the whole dependent subtree back from the same descriptors.
        Check.NotNull(manager.AddAssembly(SamplePaths.FeaturePlugin), "feature plugin could not be re-added.");
        Check.NotNull(manager.AddAssembly(SamplePaths.TopPlugin), "top plugin could not be re-added.");
        Check.True(manager.LoadAll().Success, "feature/top should load again.");

        var activationsBefore = Check.Count(host.PluginEvents, "sample.feature:Activate");
        Check.True(manager.Reload("sample.feature"), "feature should reload.");
        Check.True(manager.IsLoaded("sample.feature") && manager.IsLoaded("sample.top"), "reload must restore the subtree.");
        Check.Equal(activationsBefore + 1, Check.Count(host.PluginEvents, "sample.feature:Activate"), "reload must create a fresh activation.");
        Check.Equal(2, point.Count, "the reloaded subtree must observe the same shared point.");

        manager.Shutdown();

        Check.Equal(0, manager.Plugins.Count, "shutdown must unload every plugin.");
        Check.False(manager.IsLoaded("sample.app"), "the application plugin must be gone after shutdown.");
        Check.True(
            Check.IndexOf(host.PluginEvents, "sample.tracker:Dispose") <
            Check.IndexOf(host.PluginEvents, "sample.app:OnHostShutdown"),
            "application shutdown must run after regular plugins are disposed.");
        Check.True(Check.IndexOf(host.PluginEvents, "sample.app:OnHostShutdown") >= 0, "OnHostShutdown did not run.");

        var finalBroadcasts = Check.CountStartingWith(host.PluginEvents, "sample.app:message:");
        var transcript = host.PluginEvents.LastOrDefault(item =>
            item.StartsWith("sample.app:transcript:", StringComparison.Ordinal));
        Check.True(
            transcript == $"sample.app:transcript:{finalBroadcasts}",
            $"the hub transcript must contain every broadcast. Expected {finalBroadcasts}, event was '{transcript}'.");

        Console.WriteLine("  message flow:");
        var flow = host.PluginEvents
            .Where(item =>
                item.StartsWith("sample.app:message:", StringComparison.Ordinal) ||
                item.Contains(":receive:", StringComparison.Ordinal))
            .ToList();

        foreach (var item in flow.Take(13))
            Console.WriteLine($"    {item}");

        if (flow.Count > 13)
            Console.WriteLine($"    ... {flow.Count - 13} more events across re-add/reload ...");

        if (transcript is not null)
            Console.WriteLine($"    {transcript}");

        var leaked = manager.VerifyUnloaded();
        Check.Equal(0, leaked.Count, $"leaked load contexts: {string.Join(", ", leaked)}");
    }

    /// <summary>
    /// In-process application lifecycle: dependency order across application plugins, OnEnable
    /// after all OnLoad/Activate calls, and reverse-order shutdown.
    /// </summary>
    public static void ApplicationLifetimeOrder()
    {
        var host = new ScenarioHost();
        using var manager = new PluginManager(host);

        manager.Add(InProcess(
            "app.a",
            lifetime: PluginLifetime.Application,
            factory: context => new TraceApplicationPlugin(host, "app.a", context)));

        manager.Add(InProcess(
            "app.b",
            lifetime: PluginLifetime.Application,
            dependencies: [new PluginDependency("app.a")],
            factory: context => new TraceApplicationPlugin(host, "app.b", context)));

        manager.Add(InProcess(
            "regular",
            dependencies: [new PluginDependency("app.b")],
            factory: context => new TrackingPlugin(host, "regular", context)));

        var report = manager.LoadAll();
        Check.True(report.Success, Describe(report));

        Check.Sequence(
            host.PluginEvents,
            "app.a:OnLoad",
            "app.a:Activate",
            "app.b:OnLoad",
            "app.b:Activate",
            "app.a:OnEnable",
            "app.b:OnEnable",
            "regular:Activate");

        manager.Shutdown();

        var tail = host.PluginEvents.Skip(host.PluginEvents.Count - 3);
        Check.Sequence(tail, "regular:Dispose", "app.b:OnHostShutdown", "app.a:OnHostShutdown");
    }

    /// <summary>
    /// The context is a constructor dependency: it is the same object during construction, activation
    /// and teardown. A constructor that fails after registering is still rolled back, even though
    /// registration in a constructor is not the recommended pattern.
    /// </summary>
    public static void ConstructorInjection()
    {
        var host = new ScenarioHost();
        using (var manager = new PluginManager(host))
        {
            manager.Add(InProcess(
                "ctor.probe",
                factory: context => new ConstructorProbePlugin(host, context)));

            var report = manager.LoadAll();
            Check.True(report.Success, Describe(report));
            Check.Sequence(host.PluginEvents, "ctor.probe:Constructed", "ctor.probe:Activate");

            Check.True(manager.Unload("ctor.probe"), "the probe plugin should unload.");
            Check.Sequence(
                host.PluginEvents,
                "ctor.probe:Constructed",
                "ctor.probe:Activate",
                "ctor.probe:Dispose");
        }

        var failing = new ScenarioHost();
        using (var manager = new PluginManager(failing))
        {
            manager.Add(InProcess(
                "ctor.throws",
                factory: context => new ConstructorFailurePlugin(failing, context)));

            var report = manager.LoadAll();

            Check.False(report.Success, "a throwing constructor must fail activation.");
            Check.False(manager.IsLoaded("ctor.throws"), "the failed plugin must not be active.");
            Check.True(
                report.Failed.Any(failure =>
                    failure.PluginId == "ctor.throws" &&
                    failure.Reason.Contains("constructor failure", StringComparison.Ordinal)),
                "the constructor's own exception must be reported.");
            Check.Sequence(failing.PluginEvents, "ctor.throws:Constructed", "ctor.throws:Dispose");
        }
    }

    /// <summary>Every invalid graph shape is rejected before the affected plugin activates.</summary>
    public static void DependencyValidation()
    {
        MissingDependencyIsReportedForDependents();
        CycleIsRejected();
        MinimumVersionIsEnforced();
        ApplicationPluginMayNotDependOnReloadable();
        ApplicationPluginAddedAfterStartupIsRejected();
    }

    /// <summary>
    /// The normal extension point identity is the contribution type; the contract attribute
    /// supplies the stable string. A key is only needed for multiple points per contract.
    /// </summary>
    public static void TypedPointIdentity()
    {
        EnsureSamplePluginsExist(SamplePaths.PointsPlugin);

        using var manager = new PluginManager(new ScenarioHost());

        Check.NotNull(
            manager.AddAssembly(SamplePaths.PointsPlugin),
            "the points plugin assembly did not declare metadata.");

        var report = manager.LoadAll();
        Check.True(report.Success, Describe(report));

        Check.True(
            manager.GetPoint("experimental.demo.notes") is null,
            "no default point should exist; this plugin only defined keyed variants.");

        var left = manager.GetPoint("experimental.demo.notes#left");
        var right = manager.GetPoint("experimental.demo.notes#right");

        Check.NotNull(left, "the left point is missing.");
        Check.NotNull(right, "the right point is missing.");
        Check.Equal("experimental.demo.notes#left", left!.Id, "keyed ids must be derived automatically.");
        Check.Equal("experimental.demo.notes#right", right!.Id, "keyed ids must be derived automatically.");
        Check.Equal(
            "Layeron.Plugin.Experimental.Sample.Points",
            left.ContributionType.Assembly.GetName().Name,
            "the contract type must come from the plugin assembly, not from the host.");
        Check.Sequence(Texts(left), "left");
        Check.Sequence(Texts(right), "right");

        // The host has no reference to the contract, so it reads the contribution by reflection.
        static IEnumerable<string> Texts(IExtensionPoint point) =>
            point.Items.Select(item => (string)item.GetType().GetProperty("Text")!.GetValue(item)!);
    }

    /// <summary>
    /// A plugin acting as a loader: an application plugin registers a source, its children join
    /// the normal dependency graph, and ReloadSource rebuilds only that child subtree.
    /// </summary>
    public static void ChildLoaders()
    {
        EnsureSamplePluginsExist(
            SamplePaths.LoaderPlugin,
            SamplePaths.BadLoaderPlugin,
            SamplePaths.AppChildLoaderPlugin);

        var host = new ScenarioHost();
        using (var manager = new PluginManager(host))
        {
            Check.NotNull(
                manager.AddAssembly(SamplePaths.LoaderPlugin),
                "the sample loader assembly did not declare plugin metadata.");

            var report = manager.LoadAll();
            Check.True(report.Success, Describe(report));

            Check.True(manager.IsLoaded("sample.loader"), "the loader itself must be active.");
            Check.True(manager.IsLoaded("child.one") && manager.IsLoaded("child.two"),
                "the source's children must be active.");
            Check.True(manager.SourceIds.Contains("sample.loader.children"), "the source was not registered.");
            Check.Sequence(manager.Plugins.Select(plugin => plugin.Id),
                "sample.loader", "child.one", "child.two");

            // A loader's children must be described with the same fields as host-discovered plugins.
            var loaderInfo = manager.Plugins.Single(plugin => plugin.Id == "sample.loader");
            var children = manager.Plugins.Where(plugin => plugin.FromLoader).ToList();

            Check.False(loaderInfo.FromLoader, "host discovery must not report a loader source.");
            Check.Equal(PluginDescriptor.HostSourceId, loaderInfo.SourceId,
                "host discovery must report the reserved host source id.");
            Check.Equal("Sample Child Loader", loaderInfo.Name, "assembly metadata must fill the loader name.");
            Check.Equal(PluginApi.Version, loaderInfo.ApiVersion, "the loader must expose the host API version.");
            Check.Equal(2, children.Count, "both children must report their loader source.");
            Check.True(children.All(plugin => plugin.SourceId == "sample.loader.children"),
                "children must name the source that discovered them.");
            Check.True(children.All(plugin => !string.IsNullOrWhiteSpace(plugin.Name)),
                "children must expose a display name like host plugins do.");
            Check.True(children.All(plugin => plugin.Version == "1.0.0"),
                "children must expose their version.");
            Check.True(children.All(plugin => plugin.ApiVersion == PluginApi.Version),
                "children must expose an API version.");

            var activationsBefore = Check.Count(host.PluginEvents, "child.one:Activate");
            var reload = manager.ReloadSource("sample.loader.children");
            Check.True(reload.Success, Describe(reload));
            Check.Equal(activationsBefore + 1, Check.Count(host.PluginEvents, "child.one:Activate"),
                "reloading a source must recreate its children.");
            Check.True(manager.IsLoaded("child.two"), "the dependent child must come back too.");

            Check.False(manager.Unload("sample.loader"),
                "a loader is application-lifetime and cannot unload.");

            Check.True(manager.Unload("child.one"), "a child plugin is reloadable.");
            Check.False(manager.IsLoaded("child.two"), "child.two must cascade with child.one.");
            Check.False(manager.LoadAll().Loaded.Contains("child.one"),
                "a manually unloaded child must not be rediscovered until the source is reloaded.");

            var restored = manager.ReloadSource("sample.loader.children");
            Check.True(restored.Success, Describe(restored));
            Check.True(manager.IsLoaded("child.one") && manager.IsLoaded("child.two"),
                "ReloadSource must restore the full child subtree.");

            Check.NotNull(
                manager.AddAssembly(SamplePaths.BadLoaderPlugin),
                "the bad loader assembly did not declare metadata.");

            var badLoader = manager.LoadAll();
            Check.True(
                badLoader.Failed.Any(failure =>
                    failure.PluginId == "bad.loader" &&
                    failure.Reason.Contains("application-lifetime", StringComparison.Ordinal)),
                "a reloadable plugin must not be allowed to act as a loader.");
            Check.False(manager.IsLoaded("bad.loader"), "the rejected loader must not be active.");
        }

        // Application plugins discovered by a source are rejected: they belong to host startup,
        // not to a loader's dynamic set.
        var host2 = new ScenarioHost();
        using var manager2 = new PluginManager(host2);
        Check.NotNull(
            manager2.AddAssembly(SamplePaths.AppChildLoaderPlugin),
            "the application child loader assembly did not declare metadata.");

        var appChild = manager2.LoadAll();
        Check.True(
            appChild.Failed.Any(failure =>
                failure.PluginId == "child.app" &&
                failure.Reason.Contains("reloadable", StringComparison.Ordinal)),
            "a source must not publish application-lifetime plugins.");
        Check.False(manager2.IsLoaded("child.app"), "the rejected child must not be active.");
    }

    private static void MissingDependencyIsReportedForDependents()
    {
        using var manager = new PluginManager(new ScenarioHost());
        manager.Add(InProcess("a", dependencies: [new PluginDependency("missing")]));
        manager.Add(InProcess("b", dependencies: [new PluginDependency("a")]));

        var report = manager.LoadAll();

        Check.False(report.Success, "a missing dependency must fail the load.");
        Check.True(
            report.Failed.Any(failure =>
                failure.PluginId == "a" && failure.Reason.Contains("Missing dependency", StringComparison.Ordinal)),
            "the missing dependency was not reported.");
        Check.True(
            report.Failed.Any(failure =>
                failure.PluginId == "b" && failure.Reason.Contains("not loaded", StringComparison.Ordinal)),
            "the dependent of a failed plugin must also fail.");
        Check.Equal(0, manager.Plugins.Count, "nothing may be activated.");
    }

    private static void CycleIsRejected()
    {
        using var manager = new PluginManager(new ScenarioHost());
        manager.Add(InProcess("a", dependencies: [new PluginDependency("b")]));
        manager.Add(InProcess("b", dependencies: [new PluginDependency("a")]));

        var report = manager.LoadAll();

        Check.Equal(2, report.Failed.Count, "both cycle members must fail.");
        Check.True(
            report.Failed.All(failure => failure.Reason.Contains("cycle", StringComparison.OrdinalIgnoreCase)),
            "the failure reason should identify a cycle.");
        Check.Equal(0, manager.Plugins.Count, "nothing may be activated.");
    }

    private static void MinimumVersionIsEnforced()
    {
        using var manager = new PluginManager(new ScenarioHost());
        manager.Add(InProcess("a", version: "1.0.0"));
        manager.Add(InProcess("b", version: "1.0.0", dependencies: [new PluginDependency("a", "2.0.0")]));

        var report = manager.LoadAll();

        Check.True(report.Loaded.Contains("a"), "the dependency itself should load.");
        Check.True(
            report.Failed.Any(failure =>
                failure.PluginId == "b" && failure.Reason.Contains("below the required", StringComparison.Ordinal)),
            "the version requirement was not enforced.");
        Check.False(manager.IsLoaded("b"), "the dependent must not load.");
    }

    private static void ApplicationPluginMayNotDependOnReloadable()
    {
        using var manager = new PluginManager(new ScenarioHost());
        manager.Add(InProcess("regular"));
        manager.Add(InProcess(
            "app",
            lifetime: PluginLifetime.Application,
            dependencies: [new PluginDependency("regular")],
            factory: context => new NoopApplicationPlugin(context)));

        var report = manager.LoadAll();

        Check.True(
            report.Failed.Any(failure =>
                failure.PluginId == "app" &&
                failure.Reason.Contains("cannot depend on reloadable", StringComparison.Ordinal)),
            "application -> reloadable dependency must be rejected.");
        Check.True(manager.IsLoaded("regular"), "the regular plugin itself is valid and should load.");
    }

    private static void ApplicationPluginAddedAfterStartupIsRejected()
    {
        using var manager = new PluginManager(new ScenarioHost());
        manager.Add(InProcess("regular"));
        Check.True(manager.LoadAll().Success, "the first load should succeed.");

        manager.Add(InProcess(
            "late.app",
            lifetime: PluginLifetime.Application,
            factory: context => new NoopApplicationPlugin(context)));

        var report = manager.LoadAll();

        Check.True(
            report.Failed.Any(failure =>
                failure.PluginId == "late.app" &&
                failure.Reason.Contains("before the first load", StringComparison.Ordinal)),
            "a late application plugin must be rejected.");
    }

    private static PluginDescriptor InProcess(
        string id,
        string version = "1.0.0",
        PluginLifetime lifetime = PluginLifetime.Reloadable,
        IReadOnlyList<PluginDependency>? dependencies = null,
        Func<IPluginContext, IPlugin>? factory = null) =>
        new()
        {
            Id = id,
            Name = id,
            Version = version,
            ApiVersion = PluginApi.Version,
            Lifetime = lifetime,
            Dependencies = dependencies ?? [],
            Factory = factory ?? (context => new NoopPlugin(context))
        };

    private static string Describe(LoadReport report)
    {
        if (report.Success)
            return "load succeeded.";

        return "load failed: " + string.Join(
            "; ",
            report.Failed.Select(failure => $"{failure.PluginId}: {failure.Reason}"));
    }

    private static void EnsureSamplePluginsExist(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Sample plugin not built: '{path}'. Build the solution (or this project) first.");
            }
        }
    }
}


