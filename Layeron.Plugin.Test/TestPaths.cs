namespace Layeron.Plugin.Test;

/// <summary>
/// Locates the built sample plugins. The tests deliberately load the real assemblies from the same
/// build configuration the test is running in, so what is verified is the artifact a user gets.
/// </summary>
internal static class TestPaths
{
    private static readonly string Configuration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>Repository root, found by walking up to the solution file.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ThresholdPluginAssembly { get; } =
        Path.Combine(RepositoryRoot, "Layeron.Plugins.Threshold", "bin", Configuration, "net10.0",
            "Layeron.Plugins.Threshold.dll");

    public static string ScriptHostPluginAssembly { get; } =
        Path.Combine(RepositoryRoot, "Layeron.Plugins.ScriptHost", "bin", Configuration, "net10.0",
            "Layeron.Plugins.ScriptHost.dll");

    public static string Model3DPluginAssembly { get; } =
        Path.Combine(RepositoryRoot, "Layeron.Plugins.Model3D", "bin", Configuration, "net10.0",
            "Layeron.Plugins.Model3D.dll");

    /// <summary>
    /// The ONNX runner. The test assembly references this plugin so it can drive the runner's internals, and loads
    /// its built assembly by explicit path so the plugin loader is exercised exactly as a user's plugin would be.
    /// </summary>
    public static string OnnxPluginAssembly { get; } =
        Path.Combine(RepositoryRoot, "Layeron.Plugins.OnnxRuntime", "bin", Configuration, "net10.0",
            "Layeron.Plugins.OnnxRuntime.dll");

    /// <summary>
    /// The agent plugin. Referenced for its option store, and loaded by path so activation itself is covered:
    /// activation is where the data directory is read, which is the one part of the plugin that is not UI-bound.
    /// </summary>
    public static string AgentPluginAssembly { get; } =
        Path.Combine(RepositoryRoot, "Layeron.Plugins.Agent", "bin", Configuration, "net10.0",
            "Layeron.Plugins.Agent.dll");

    /// <summary>Creates a throwaway plugin directory for sources that scan folders.</summary>
    public static string CreateTemporaryPluginDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "layeron-plugin-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Layeron.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repository root from '{AppContext.BaseDirectory}'.");
    }
}
