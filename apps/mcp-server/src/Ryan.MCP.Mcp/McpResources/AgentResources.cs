using System.ComponentModel;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services;

namespace Ryan.MCP.Mcp.McpResources;

[McpServerResourceType]
public sealed class AgentResources(AgentIngestionCoordinator agents)
{
    [McpServerResource(UriTemplate = "agents://{name}", Name = "agent_content", MimeType = "text/markdown")]
    [Description("Read the full markdown content of an agent/skill/instruction by name. Use agents://list to discover available agent names.")]
    public string GetAgentContent([Description("Agent name as returned by list_agents")] string name)
    {
        var agent = agents.GetAgent(name);
        if (agent == null)
        {
            return $"# Agent Not Found\n\nNo agent named '{name}' is indexed. Use the `list_agents` tool to see available agents.";
        }

        return agent.RawContent;
    }

    [McpServerResource(UriTemplate = "agents://list", Name = "agents_list", MimeType = "text/markdown")]
    [Description("Read the full list of all available agents as a markdown document. Useful for browsing what is available.")]
    public string GetAgentsList()
    {
        var snapshot = agents.Snapshot;
        if (snapshot.TotalAgents == 0)
        {
            return "# Agent Library\n\nNo agents are currently indexed. Trigger an ingest to populate the library.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Agent Library");
        sb.AppendLine();
        sb.AppendLine($"**{snapshot.TotalAgents} agents indexed** | Last updated: {snapshot.UpdatedUtc:u}");
        sb.AppendLine();

        foreach (var group in snapshot.Agents.GroupBy(a => a.Scope).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            foreach (var agent in group.OrderBy(a => a.Name))
            {
                sb.AppendLine($"- **{agent.Name}** ({agent.Format}) — {agent.Description}");
                if (agent.Tags.Count > 0)
                {
                    sb.AppendLine($"  Tags: {string.Join(", ", agent.Tags)}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
