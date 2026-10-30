using Microsoft.Agents.AI;
using System.ComponentModel;
namespace Layeron.Plugins.Agent.Chat;

static class ChatSkills
{
    public static AgentSkillsProvider BuildInSkills =>
        new(new AgentInlineSkill(
            name: "layeron-skills",
            description: "The skills about how to work with the layeron app to help the user to done the task",
            instructions: "The app provide a reage of python api app for accsee the data, you can check the docs with the tool GetDocs, follow the name in folling names:\n" +
                string.Join("\n", BuildDocsNames.Select(name => $"- {name}")) +
                "\n\nThe data API covers the loaded volume, mask layers and annotation layers. " +
                "Create annotations with annotation_layer_create and its add_point/add_line/add_polygon methods."
        ));

    public static string[] BuildDocsNames => typeof(ChatSkills).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Layeron.Plugins.Agent.Docs."))
        .Select(name => name.Substring("Layeron.Plugins.Agent.Docs.".Length))
        .ToArray();
}

partial class ChatTools
{
    [Description("Get the documentation for a specific topic.")]
    string GetDocs([Description("The id of the documentation to retrieve")]string name)
    {
        var resourceName = $"Layeron.Plugins.Agent.Docs.{name}";
        using var stream = typeof(ChatSkills).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return $"Documentation for '{name}' not found.";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

}