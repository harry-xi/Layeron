using System;
using System.IO;

namespace Layeron.App.Test;

/// <summary>A throwaway folder that deletes itself, so a test never depends on or damages real state.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "layeron-app-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>A path inside the folder; the file is not created.</summary>
    public string Path(string name) => System.IO.Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // A locked file must not turn a passing test into a failing one.
        }
    }
}
