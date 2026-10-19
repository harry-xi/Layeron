using Microsoft.Agents.AI;

namespace Layeron.Plugins.Agent.Chat;

static class ChatSkills
{
    public static AgentSkillsProvider BuildInSkills =>
        new(new AgentInlineSkill(
            name: "python api",
            description: "The app provides a python api for access app data",
            instructions: "Use this api to access the app's data in python."
        ));
}