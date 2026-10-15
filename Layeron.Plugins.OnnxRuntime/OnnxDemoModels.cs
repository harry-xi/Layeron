using System.Reflection;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// Writes the bundled demo models into the model folder on first use.
/// <para>
/// Embedded resources rather than loose files in the output, so they travel with the assembly that installs them
/// and cannot drift from the code that reads them. Deployed only when the file is absent, so a user who replaced
/// or deleted a demo model keeps that decision - an update that silently brought it back would be a bug, not a
/// feature.
/// </para>
/// </summary>
internal static class OnnxDemoModels
{
    /// <summary>
    /// Logical resource names. Spelled out rather than derived from a file path so renaming the file on disk
    /// cannot silently change what the plugin looks for inside its own assembly.
    /// </summary>
    private static readonly (string ResourceName, string FileName)[] Models =
    [
        ("Layeron.Plugins.OnnxRuntime.demo.structure.slice.onnx", "demo_structure_slice.onnx"),
        ("Layeron.Plugins.OnnxRuntime.demo.structure.volume.onnx", "demo_structure_volume.onnx")
    ];

    /// <summary>
    /// Deploys any missing demo model into <paramref name="folder"/> and returns the file names it wrote. A
    /// failure is reported instead of thrown: a read-only data folder should degrade to "no demo model", not stop
    /// the plugin from loading.
    /// </summary>
    public static IReadOnlyList<string> Deploy(string folder, Action<string>? onError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var written = new List<string>();
        var assembly = typeof(OnnxDemoModels).Assembly;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
            return written;
        }

        foreach (var (resourceName, fileName) in Models)
        {
            var target = Path.Combine(folder, fileName);
            if (File.Exists(target))
                continue;

            try
            {
                using var source = assembly.GetManifestResourceStream(resourceName);
                if (source is null)
                {
                    // A missing resource is a build problem, not a user problem, so it is worth saying out loud.
                    onError?.Invoke($"内置模型资源缺失：{resourceName}");
                    continue;
                }

                using var file = File.Create(target);
                source.CopyTo(file);
                written.Add(fileName);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"写入 {fileName} 失败：{ex.Message}");
            }
        }

        return written;
    }
}
