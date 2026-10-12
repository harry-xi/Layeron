namespace Layeron.Plugins.PythonRunner;

public interface IPythonFileEditorService
{
    Task OpenFileAsync(string? path = null);
}