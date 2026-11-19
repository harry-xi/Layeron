namespace Layeron.Plugin.Experimental;

/// <summary>Tiny assertion helper: the demo is the test suite, so it fails with a readable message.</summary>
internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }

    public static void NotNull<T>(T? value, string message) where T : class
    {
        if (value is null)
            throw new InvalidOperationException(message);
    }

    public static void Sequence(IEnumerable<string> actual, params string[] expected)
    {
        var values = actual.ToArray();
        if (!values.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected sequence. Expected [{string.Join(", ", expected)}]; " +
                $"actual [{string.Join(", ", values)}].");
        }
    }

    public static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    public static int Count(IReadOnlyList<string> values, string value) =>
        values.Count(item => string.Equals(item, value, StringComparison.Ordinal));

    public static int CountStartingWith(IReadOnlyList<string> values, string prefix) =>
        values.Count(item => item.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>Locates the sample plugin assemblies built next to the main project.</summary>
internal static class SamplePaths
{
    private static readonly string Configuration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ApplicationPlugin { get; } = PluginPath("Layeron.Plugin.Experimental.Sample.App");

    public static string FeaturePlugin { get; } = PluginPath("Layeron.Plugin.Experimental.Sample.Feature");

    public static string TopPlugin { get; } = PluginPath("Layeron.Plugin.Experimental.Sample.Top");

    public static string LoaderPlugin { get; } = PluginPath("Layeron.Plugin.Experimental.Sample.Loader");

    public static string PointsPlugin { get; } = PluginPath("Layeron.Plugin.Experimental.Sample.Points");

    public static string BadLoaderPlugin { get; } = PluginPath("Layeron.Plugin.Experimental.Sample.BadLoader");

    public static string AppChildLoaderPlugin { get; } = PluginPath("Layeron.Plugin.Experimental.Sample.AppChildLoader");

    /// <summary>
    /// The plugin-published contract that the host loads into its shared contract context. It is
    /// deployed next to the demo executable, not referenced by the demo assembly.
    /// </summary>
    public static string SharedContract { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "plugins.shared",
        "Layeron.Plugin.Experimental.Sample.Actions.Api.dll");

    private static string PluginPath(string projectName) =>
        Path.Combine(
            RepositoryRoot,
            projectName,
            "bin",
            Configuration,
            "net10.0",
            $"{projectName}.dll");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Layeron.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root from '{AppContext.BaseDirectory}'.");
    }
}


