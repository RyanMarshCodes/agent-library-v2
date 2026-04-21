using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class ConnectorTools(ExternalConnectorRegistry connectors, ExternalMcpClientService externalMcp, ILogger<ConnectorTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "list_external_connectors")]
    [Description("List all configured external MCP connectors (e.g. Azure DevOps, GitHub, Docker) and whether they are enabled.")]
    public string ListExternalConnectors()
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ConnectorTools.ListExternalConnectors",
        });
        logger.LogDebug("ListExternalConnectors invoked");

        return JsonSerializer.Serialize(new
        {
            configuredCount = connectors.Configured.Count,
            enabledCount = connectors.Enabled.Count,
            connectors = connectors.Configured.Select(c => new
            {
                c.Name,
                c.Enabled,
                c.Transport,
                c.Endpoint,
                c.TimeoutMs,
            }),
        }, JsonOptions);
    }

    [McpServerTool(Name = "list_external_mcp_tools")]
    [Description(
        "List tools exposed by an enabled external MCP connector (e.g. the 'memory' knowledge-graph server). " +
        "Use connector name from list_external_connectors, then call_external_mcp_tool to invoke a tool.")]
    public Task<string> ListExternalMcpTools(
        [Description("External connector name, e.g. 'memory'")] string connector,
        CancellationToken cancellationToken = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ConnectorTools.ListExternalMcpTools",
            ["Connector"] = connector,
        });
        logger.LogDebug("ListExternalMcpTools invoked");
        return externalMcp.ListToolsAsync(connector, cancellationToken);
    }

    [McpServerTool(Name = "call_external_mcp_tool")]
    [Description(
        "Invoke a tool on an enabled external MCP connector (same URL as in appsettings ExternalConnectors). " +
        "For the memory connector, typical tools include search_nodes, read_graph, create_entities, create_relations, add_observations. " +
        "Use list_external_mcp_tools to discover names and schemas.")]
    public Task<string> CallExternalMcpTool(
        [Description("External connector name, e.g. 'memory'")] string connector,
        [Description("Downstream tool name exactly as returned by list_external_mcp_tools")] string tool,
        [Description("JSON object of arguments, e.g. {\"query\":\"topic\"}. Omit or use null for no arguments.")] string? argumentsJson = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ConnectorTools.CallExternalMcpTool",
            ["Connector"] = connector,
            ["Tool"] = tool,
        });
        logger.LogDebug("CallExternalMcpTool invoked");
        return externalMcp.CallToolAsync(connector, tool, argumentsJson, cancellationToken);
    }
}
