using Layeron.Plugins.Agent.Chat;
using Microsoft.Extensions.AI;

namespace Layeron.Plugin.Test;

/// <summary>
/// The agent's documentation tools read Markdown from the plugin assembly, and the resource name is the
/// contract between the build and <see cref="ChatSkills"/>. SDK projects do not embed arbitrary files by
/// default, so this is the test that catches a document that exists on disk but is missing from the DLL.
/// </summary>
[TestClass]
public sealed class AgentDocsTests
{
    private const string PythonApiResource = "Layeron.Plugins.Agent.Docs.python_api.md";

    [TestMethod]
    public void TheDocs_AreEmbeddedWhereTheAgentLooksForThem()
    {
        var names = typeof(ChatSkills).Assembly.GetManifestResourceNames();

        CollectionAssert.Contains(names, PythonApiResource);
        CollectionAssert.Contains(ChatSkills.BuildDocsNames, "python_api.md");
    }

    [TestMethod]
    public async Task TheGetDocsTool_ReadsTheEmbeddedDocument()
    {
        var tools = new ChatTools(null!, null!, null!).BuildTools();
        var getDocs = tools.OfType<AIFunction>().Single(tool => tool.Name == "GetDocs");

        var result = await getDocs.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["name"] = "python_api.md" }));

        StringAssert.Contains(result?.ToString(), "Layeron's Python API");
    }
}
