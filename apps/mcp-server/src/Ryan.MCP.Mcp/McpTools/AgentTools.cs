using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Configuration;
using Ryan.MCP.Mcp.Services;
using Ryan.MCP.Mcp.Services.ModelMapping;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class AgentTools(
    AgentIngestionCoordinator agents,
    IModelMappingStore modelMappings,
    McpOptions options,
    ILogger<AgentTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "list_agents")]
    [Description("List all available agents, skills, and instructions indexed by this MCP server. Returns name, description, scope, tags, and format for each. Example: call without args to get all, or with scope='backend' to filter.")]
    public string ListAgents(
        [Description("Filter by scope (e.g. 'refactoring', 'security', 'testing'). Omit to list all.")] string? scopeFilter = null,
        [Description("Comma-separated tags to filter by (e.g. 'csharp,dotnet'). Omit to list all.")] string? tags = null)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "AgentTools.ListAgents",
            ["Scope"] = scopeFilter,
            ["Tags"] = tags,
        });
        logger.LogDebug("ListAgents invoked");

        var tagList = string.IsNullOrWhiteSpace(tags)
            ? new List<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();

        var results = agents.ListAgents(scopeFilter, tagList);
        return JsonSerializer.Serialize(new
        {
            count = results.Count,
            scope = scopeFilter,
            tags = tagList,
            agents = results.Select(a => new
            {
                a.Name,
                a.Description,
                a.Scope,
                a.Tags,
            }),
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_agent")]
    [Description("Get the full content of a specific agent/skill/instruction by name. Returns the complete markdown including frontmatter and body.")]
    public string GetAgent([Description("The agent name exactly as returned by list_agents")] string name)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "AgentTools.GetAgent",
            ["Name"] = name,
        });
        logger.LogDebug("GetAgent invoked");

        if (string.IsNullOrWhiteSpace(name))
        {
            return JsonSerializer.Serialize(new { error = "name is required" });
        }

        var agent = agents.GetAgent(name);
        if (agent == null)
        {
            return JsonSerializer.Serialize(new { error = $"Agent '{name}' not found. Use list_agents to see available agents." });
        }

        return JsonSerializer.Serialize(new
        {
            agent.Name,
            agent.Description,
            agent.Scope,
            agent.Tags,
            agent.Format,
            agent.RawContent,
            agent.IndexedUtc,
        }, JsonOptions);
    }

    [McpServerTool(Name = "search_agents")]
    [Description("Search agents by keyword across name, description, tags, and filename. Returns scored results sorted by relevance. Use to find the right agent for a task — try multiple terms like 'csharp test' or 'api design'.")]
    public string SearchAgents([Description("Search query, e.g. 'security audit' or 'csharp refactor' or 'react frontend'")] string query)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "AgentTools.SearchAgents",
            ["Query"] = query,
        });
        logger.LogDebug("SearchAgents invoked");

        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(new { error = "query is required" });
        }

        var results = agents.SearchAgents(query);
        return JsonSerializer.Serialize(new
        {
            query,
            count = results.Count,
            agents = results.Select(a => new
            {
                a.Name,
                a.Description,
                a.Scope,
                a.Tags,
            }),
        }, JsonOptions);
    }

    [McpServerTool(Name = "list_agent_scopes")]
    [Description("List all available agent scopes and formats, with counts. Use this to understand what categories of agents are available.")]
    public string ListAgentScopes()
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "AgentTools.ListAgentScopes",
        });
        logger.LogDebug("ListAgentScopes invoked");

        var snapshot = agents.Snapshot;
        return JsonSerializer.Serialize(new
        {
            totalAgents = snapshot.TotalAgents,
            scopes = snapshot.ByScope.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
            formats = snapshot.ByFormat.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
            scanRoots = snapshot.ScanRoots,
        }, JsonOptions);
    }

    [McpServerTool(Name = "agent_status")]
    [Description("Get the current agent ingestion status including when agents were last indexed and how many are available.")]
    public string AgentStatus()
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "AgentTools.AgentStatus",
        });
        logger.LogDebug("AgentStatus invoked");

        var snapshot = agents.Snapshot;
        return JsonSerializer.Serialize(new
        {
            snapshot.ProjectSlug,
            snapshot.LastStartedUtc,
            snapshot.UpdatedUtc,
            snapshot.TotalAgents,
            snapshot.ByScope,
            snapshot.ByFormat,
            snapshot.ScanRoots,
        }, JsonOptions);
    }

    [McpServerTool(Name = "recommend_agent")]
    [Description("Get the best agent recommendation for a specific task. Returns score, reasons, model recommendation, and activation instructions. Best for: 'fix my C# bug', 'design an API', 'migrate to .NET 8', 'write tests for my react component'.")]
    public async Task<string> RecommendAgent(
        [Description("Task description in natural language, e.g. 'security audit of my C# API' or 'refactor this class' or 'write unit tests'")] string task,
        [Description("Optional AI client tool (e.g. 'opencode', 'copilot', 'claude'). If omitted, uses McpOptions.ModelMapping.ActiveClientTool.")] string? clientTool,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "AgentTools.RecommendAgent",
            ["Task"] = task,
        });
        logger.LogDebug("RecommendAgent invoked");

        if (string.IsNullOrWhiteSpace(task))
            return JsonSerializer.Serialize(new { error = "task description is required" });

        var results = agents.RecommendAgents(task);
        if (results.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                message = "No specific agent found for this task.",
                hint = "Use list_agents to browse all available agents, or search_agents with different keywords.",
            });
        }

        var top = results.First();

        // Look up model mapping for the top recommendation
        var mapping = await modelMappings.GetAsync(top.Agent.Name, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            task,
            recommendation = new
            {
                top.Agent.Name,
                top.Agent.Description,
                top.Agent.Scope,
                top.Agent.Tags,
                top.Score,
                top.Reasons,
                model = mapping is not null
                    ? BuildModelRecommendation(mapping, clientTool)
                    : null,
                fetch = $"get_agent(\"{top.Agent.Name}\")",
            },
            alternatives = results.Skip(1).Take(3).Select(a => new
            {
                a.Agent.Name,
                a.Agent.Description,
                a.Score,
                fetch = $"get_agent(\"{a.Agent.Name}\")",
            }),
        }, JsonOptions);
    }

    [McpServerTool(Name = "ingest_agents")]
    [Description("Trigger a re-scan and re-index of all agent files. Use this after adding or modifying agent files.")]
    public async Task<string> IngestAgents(CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "AgentTools.IngestAgents",
        });
        logger.LogDebug("IngestAgents invoked");

        await agents.TriggerReindexAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = agents.Snapshot;
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            snapshot.ProjectSlug,
            snapshot.UpdatedUtc,
            snapshot.TotalAgents,
            snapshot.ByScope,
            snapshot.ByFormat,
        }, JsonOptions);
    }

    private object BuildModelRecommendation(AgentModelMapping mapping, string? clientTool)
    {
        var effective = ModelToolOverrideResolver.ResolveEffectiveModel(
            mapping,
            requestedTool: clientTool,
            configuredTool: options.ModelMapping.ActiveClientTool);

        return new
        {
            primary = effective.EffectiveModel,
            mapping.Tier,
            provider = ResolveProvider(effective.EffectiveModel),
            overrideApplied = effective.UsedOverride,
            tool = effective.EffectiveTool,
            toolOverrides = ModelToolOverrideResolver.ParseToolOverrides(mapping.ToolOverridesJson),
            alternatives = new[] { mapping.AltModel1, mapping.AltModel2 }
                .Where(a => a is not null),
        };
    }

    private string? ResolveProvider(string modelName)
    {
        return options.LlmProviders
            .Where(p => p.Enabled)
            .FirstOrDefault(p => p.Models.Contains(modelName, StringComparer.OrdinalIgnoreCase))
            ?.Name;
    }
}
