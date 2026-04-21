using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services;

namespace Ryan.MCP.Mcp.McpPrompts;

[McpServerPromptType]
public sealed class AgentPrompts(AgentIngestionCoordinator agents)
{
    [McpServerPrompt(Name = "use_agent")]
    [Description("Load a specific agent/skill/instruction as a system prompt for your current session. The agent's full instructions become your active persona or task context.")]
    public ChatMessage UseAgent(
        [Description("Agent name as returned by list_agents or search_agents")] string name,
        [Description("Optional additional context to append after the agent instructions")] string? context = null)
    {
        var agent = agents.GetAgent(name);
        if (agent == null)
        {
            return new ChatMessage(
                ChatRole.User,
                $"No agent named '{name}' found. Use list_agents to see available agents.");
        }

        var content = agent.RawContent;
        if (!string.IsNullOrWhiteSpace(context))
        {
            content += $"\n\n---\n\n## Additional Context\n\n{context}";
        }

        return new ChatMessage(ChatRole.User, content);
    }

    [McpServerPrompt(Name = "use_agent_as_system")]
    [Description("Load a specific agent as a system-level instruction. Use this when you want the agent's persona to govern the entire conversation.")]
    public ChatMessage UseAgentAsSystem(
        [Description("Agent name as returned by list_agents or search_agents")] string name)
    {
        var agent = agents.GetAgent(name);
        if (agent == null)
        {
            return new ChatMessage(
                ChatRole.System,
                $"No agent named '{name}' found. Available agents can be listed with list_agents.");
        }

        return new ChatMessage(ChatRole.System, agent.RawContent);
    }
}
