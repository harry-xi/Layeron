namespace Layeron.Plugin.Experimental;

/// <summary>
/// Resolves a candidate set into a deterministic dependency-first activation order.
/// <para>
/// The resolver validates missing dependencies, version requirements, self-dependencies, the
/// application-lifetime rule (an application plugin may only depend on application plugins), the
/// plugin API version, and cycles. A node that fails validation is excluded from the plan; its
/// dependents are still planned so activation can report the concrete "dependency not loaded"
/// failure instead of hiding it.
/// </para>
/// </summary>
internal static class DependencyResolver
{
    public static DependencyPlan Resolve(
        IReadOnlyList<PluginDescriptor> candidates,
        IReadOnlyDictionary<string, PluginDescriptor> available,
        bool enforceApiVersion)
    {
        var failures = new List<PluginFailure>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            dependencies[candidate.Id] = [];

            if (candidate.Factory is null && candidate.AssemblyPath is null)
            {
                Fail(candidate.Id, "Descriptor has neither a factory nor an assembly path.");
                continue;
            }

            if (!IsApiCompatible(candidate, enforceApiVersion, out var apiProblem))
            {
                Fail(candidate.Id, apiProblem!);
                continue;
            }

            foreach (var dependency in candidate.Dependencies)
            {
                if (!available.TryGetValue(dependency.Id, out var provider))
                {
                    Fail(candidate.Id, $"Missing dependency '{dependency.Id}'.");
                    break;
                }

                if (candidate.Lifetime == PluginLifetime.Application &&
                    provider.Lifetime != PluginLifetime.Application)
                {
                    Fail(
                        candidate.Id,
                        $"Application-lifetime plugin '{candidate.Id}' cannot depend on reloadable plugin '{dependency.Id}'.");
                    break;
                }

                if (VersionProblem(provider, dependency) is { } problem)
                {
                    Fail(candidate.Id, problem);
                    break;
                }

                dependencies[candidate.Id].Add(dependency.Id);
            }
        }

        var byId = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var indegree = candidates.ToDictionary(
            candidate => candidate.Id,
            _ => 0,
            StringComparer.Ordinal);

        var dependents = candidates.ToDictionary(
            candidate => candidate.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (failed.Contains(candidate.Id))
                continue;

            foreach (var dependencyId in dependencies[candidate.Id])
            {
                // Only edges between two planned candidates affect order. Loaded dependencies are
                // already available, and failed dependencies are reported at activation.
                if (!byId.ContainsKey(dependencyId) || failed.Contains(dependencyId))
                    continue;

                indegree[candidate.Id]++;
                dependents[dependencyId].Add(candidate.Id);
            }
        }

        var ready = new SortedSet<string>(
            candidates
                .Where(candidate => !failed.Contains(candidate.Id) && indegree[candidate.Id] == 0)
                .Select(candidate => candidate.Id),
            StringComparer.Ordinal);

        var order = new List<PluginDescriptor>();
        var ordered = new HashSet<string>(StringComparer.Ordinal);

        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);

            order.Add(byId[id]);
            ordered.Add(id);

            foreach (var dependent in dependents[id])
            {
                if (--indegree[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        // Anything still unplanned is either part of a cycle or depends on one.
        var cycle = candidates
            .Where(candidate => !failed.Contains(candidate.Id) && !ordered.Contains(candidate.Id))
            .Select(candidate => candidate.Id)
            .ToList();

        if (cycle.Count > 0)
        {
            var path = string.Join(" -> ", cycle);
            foreach (var id in cycle)
                Fail(id, $"Dependency cycle: {path}.");
        }

        return new DependencyPlan(order, failures);

        void Fail(string pluginId, string reason)
        {
            if (failed.Add(pluginId))
                failures.Add(new PluginFailure(pluginId, reason));
        }
    }

    /// <summary>Version comparison used both while planning and again immediately before activation.</summary>
    public static string? VersionProblem(PluginDescriptor provider, PluginDependency dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency.MinimumVersion))
            return null;

        if (!Version.TryParse(dependency.MinimumVersion, out var minimum))
            return $"Dependency '{dependency.Id}' declares an invalid minimum version '{dependency.MinimumVersion}'.";

        if (!Version.TryParse(provider.Version, out var actual))
            return $"Dependency '{dependency.Id}' has no parseable version (found '{provider.Version}').";

        return actual < minimum
            ? $"Dependency '{dependency.Id}' is version {actual}, below the required {minimum}."
            : null;
    }

    private static bool IsApiCompatible(
        PluginDescriptor descriptor,
        bool enforce,
        out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(descriptor.ApiVersion) || !enforce)
            return true;

        var declared = descriptor.ApiVersion.Split('.')[0];
        var current = PluginApi.Version.Split('.')[0];
        if (string.Equals(declared, current, StringComparison.Ordinal))
            return true;

        problem =
            $"Plugin '{descriptor.Id}' was built for API {descriptor.ApiVersion}; host provides {PluginApi.Version}.";
        return false;
    }
}

internal sealed record DependencyPlan(
    IReadOnlyList<PluginDescriptor> Order,
    IReadOnlyList<PluginFailure> Failures);

