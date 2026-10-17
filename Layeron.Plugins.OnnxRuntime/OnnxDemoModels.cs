using System.Reflection;

namespace Layeron.Plugins.Onnx.Runtime;

/// <summary>
/// The bundled demo models, as bytes.
/// <para>
/// Embedded resources rather than loose files in the output, so they travel with the assembly that carries them
/// and cannot drift from the code that reads them. They are handed over in memory and loaded straight into an
/// ONNX session: nothing is written to disk, so there is no model folder to own, no file for a rescan to find or
/// lose, and no "bring back what the user deleted" decision to get right.
/// </para>
/// </summary>
internal static class OnnxDemoModels
{
    /// <summary>
    /// Logical resource names. Spelled out rather than derived from a file path so renaming a file on disk cannot
    /// silently change what the plugin looks for inside its own assembly.
    /// </summary>
    private static readonly (string ResourceName, string FileName)[] Models =
    [
        ("Layeron.Plugins.OnnxRuntime.demo.structure.slice.onnx", "demo_structure_slice.onnx"),
        ("Layeron.Plugins.OnnxRuntime.demo.structure.volume.onnx", "demo_structure_volume.onnx")
    ];

    /// <summary>
    /// Reads every bundled model. A resource that cannot be read is reported and skipped instead of thrown: a
    /// build whose resources went missing should cost the user the demo models, not the plugin.
    /// </summary>
    public static IReadOnlyList<(string FileName, byte[] Bytes)> Read(Action<string>? onError = null)
    {
        var assembly = typeof(OnnxDemoModels).Assembly;
        var models = new List<(string, byte[])>(Models.Length);

        foreach (var (resourceName, fileName) in Models)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    // A missing resource is a build problem, not a user problem, so it is worth saying out loud.
                    onError?.Invoke($"内置模型资源缺失：{resourceName}");
                    continue;
                }

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                models.Add((fileName, buffer.ToArray()));
            }
            catch (Exception ex)
            {
                onError?.Invoke($"读取 {fileName} 失败：{ex.Message}");
            }
        }

        return models;
    }
}
