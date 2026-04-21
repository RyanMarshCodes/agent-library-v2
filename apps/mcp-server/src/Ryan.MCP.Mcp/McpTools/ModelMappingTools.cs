using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Configuration;
using Ryan.MCP.Mcp.Services;
using Ryan.MCP.Mcp.Services.ModelMapping;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class ModelMappingTools(
    IModelMappingStore store,
    ModelMappingSyncService syncService,
    AgentIngestionCoordinator agents,
    McpOptions options,
    ILogger<ModelMappingTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "get_model_mapping")]
    [Description("Get the stored model mapping for a specific agent. Returns tier, primary model, alternatives, provider, and cost info.")]
    public async Task<string> GetModelMapping(
        [Description("Agent name exactly as indexed (e.g. 'test-writer', 'orchestrator')")] string agentName,
        CancellationToken ct)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ModelMappingTools.GetModelMapping",
            ["AgentName"] = agentName,
        });
        logger.LogDebug("GetModelMapping invoked");

        if (string.IsNullOrWhiteSpace(agentName))
        {
            return JsonSerializer.Serialize(new { error = "agentName is required" });
        }

        var mapping = await store.GetAsync(agentName.Trim(), ct).ConfigureAwait(false);
        if (mapping is null)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"No model mapping found for agent '{agentName}'",
                hint = "Run sync_model_mappings to populate from agent frontmatter, or use update_model_mapping to set manually.",
            });
        }

        return JsonSerializer.Serialize(new
        {
            mapping.AgentName,
            mapping.Tier,
            primary = new { model = mapping.PrimaryModel, provider = mapping.PrimaryProvider },
            toolOverrides = ModelToolOverrideResolver.ParseToolOverrides(mapping.ToolOverridesJson),
            alternatives = new[]
            {
                mapping.AltModel1 is not null ? new { model = mapping.AltModel1, provider = mapping.AltProvider1 } : null,
                mapping.AltModel2 is not null ? new { model = mapping.AltModel2, provider = mapping.AltProvider2 } : null,
            }.Where(a => a is not null),
            cost = mapping.CostPer1MIn.HasValue || mapping.CostPer1MOut.HasValue
                ? new { inputPer1M = mapping.CostPer1MIn, outputPer1M = mapping.CostPer1MOut }
                : null,
            mapping.Notes,
            mapping.SyncedFrom,
        }, JsonOptions);
    }

    [McpServerTool(Name = "list_model_mappings")]
    [Description("List all stored agent-model mappings, optionally filtered by tier (e.g. 'frontier', 'capable', 'strong-coding').")]
    public async Task<string> ListModelMappings(
        [Description("Filter by tier (e.g. 'frontier', 'strong-coding', 'capable'). Omit to list all.")] string? tier,
        CancellationToken ct)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ModelMappingTools.ListModelMappings",
            ["Tier"] = tier,
        });
        logger.LogDebug("ListModelMappings invoked");

        var mappings = await store.ListAsync(tier?.Trim(), ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            tier = tier ?? "all",
            count = mappings.Count,
            mappings = mappings.Select(m => new
            {
                m.AgentName,
                m.Tier,
                m.PrimaryModel,
                m.PrimaryProvider,
                toolOverrides = ModelToolOverrideResolver.ParseToolOverrides(m.ToolOverridesJson),
                m.AltModel1,
                m.AltModel2,
                m.SyncedFrom,
            }),
        }, JsonOptions);
    }

    [McpServerTool(Name = "update_model_mapping")]
    [Description("Manually set or override a model mapping for an agent. Sets synced_from='manual' so it won't be overwritten by frontmatter sync.")]
    public async Task<string> UpdateModelMapping(
        [Description("Agent name")] string agentName,
        [Description("Primary model (e.g. 'claude-opus-4-6', 'gpt-5.4-nano')")] string primaryModel,
        [Description("Tier (e.g. 'frontier', 'strong-coding', 'capable')")] string tier,
        [Description("First alternative model")] string? altModel1 = null,
        [Description("Second alternative model")] string? altModel2 = null,
        [Description("Input cost per 1M tokens")] decimal? costPer1MIn = null,
        [Description("Output cost per 1M tokens")] decimal? costPer1MOut = null,
        [Description("Free-text notes")] string? notes = null,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ModelMappingTools.UpdateModelMapping",
            ["AgentName"] = agentName,
            ["Tier"] = tier,
        });
        logger.LogDebug("UpdateModelMapping invoked");

        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(primaryModel) || string.IsNullOrWhiteSpace(tier))
        {
            return JsonSerializer.Serialize(new { error = "agentName, primaryModel, and tier are required" });
        }

        var mapping = new AgentModelMapping(
            AgentName: agentName.Trim(),
            Tier: tier.Trim(),
            PrimaryModel: primaryModel.Trim(),
            PrimaryProvider: ResolveProvider(primaryModel.Trim()),
            AltModel1: altModel1?.Trim(),
            AltProvider1: altModel1 is not null ? ResolveProvider(altModel1.Trim()) : null,
            AltModel2: altModel2?.Trim(),
            AltProvider2: altModel2 is not null ? ResolveProvider(altModel2.Trim()) : null,
            CostPer1MIn: costPer1MIn,
            CostPer1MOut: costPer1MOut,
            Notes: notes?.Trim(),
            SyncedFrom: "manual");

        await store.UpsertAsync(mapping, ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            message = $"Model mapping for '{agentName}' updated (synced_from=manual, won't be overwritten by sync)",
            mapping = new { mapping.AgentName, mapping.Tier, mapping.PrimaryModel, mapping.PrimaryProvider },
        }, JsonOptions);
    }

    [McpServerTool(Name = "sync_model_mappings")]
    [Description("Re-sync all agent model mappings from frontmatter into Postgres. Manual overrides (synced_from='manual') are preserved by default.")]
    public async Task<string> SyncModelMappings(
        [Description("If true, also overwrite manual overrides (default false)")] bool overwriteManual = false,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ModelMappingTools.SyncModelMappings",
            ["OverwriteManual"] = overwriteManual,
        });
        logger.LogDebug("SyncModelMappings invoked");

        var result = await syncService.SyncAsync(preserveManual: !overwriteManual, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            result.Synced,
            result.PartiallyParsed,
            result.Skipped,
            overwriteManual,
        }, JsonOptions);
    }

    [McpServerTool(Name = "recommend_model")]
    [Description("Recommend the best model for a task or agent. Supports per-tool overrides via frontmatter model_by_tool and optional clientTool parameter.")]
    public async Task<string> RecommendModel(
        [Description("Task description (e.g. 'write unit tests for C# service'). Required if agent_name not provided.")] string? task = null,
        [Description("Agent name to look up directly. If provided, task is ignored.")] string? agentName = null,
        [Description("Optional AI client tool (e.g. 'opencode', 'copilot', 'claude'). If omitted, uses McpOptions.ModelMapping.ActiveClientTool.")] string? clientTool = null,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ModelMappingTools.RecommendModel",
            ["Task"] = task,
            ["AgentName"] = agentName,
        });
        logger.LogDebug("RecommendModel invoked");

        if (string.IsNullOrWhiteSpace(task) && string.IsNullOrWhiteSpace(agentName))
        {
            return JsonSerializer.Serialize(new { error = "Provide either 'task' or 'agent_name' (or both)" });
        }

        // If agent_name provided, look up directly
        if (!string.IsNullOrWhiteSpace(agentName))
        {
            var mapping = await store.GetAsync(agentName.Trim(), ct).ConfigureAwait(false);
            if (mapping is null)
            {
                return JsonSerializer.Serialize(new
                {
                    error = $"No model mapping for '{agentName}'",
                    hint = "Run sync_model_mappings first, or use update_model_mapping to set manually.",
                });
            }

            return FormatRecommendation(mapping, agentName.Trim(), null, clientTool);
        }

        // Otherwise, find best agent for the task then look up its model
        var recommendations = agents.RecommendAgents(task!);
        if (recommendations.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                task,
                message = "No matching agent found for this task.",
                hint = "Try a different task description, or use list_model_mappings to browse all mappings.",
            });
        }

        var topAgent = recommendations.First();
        var agentMapping = await store.GetAsync(topAgent.Agent.Name, ct).ConfigureAwait(false);

        if (agentMapping is null)
        {
            return JsonSerializer.Serialize(new
            {
                task,
                agent = topAgent.Agent.Name,
                message = $"Agent '{topAgent.Agent.Name}' matched but has no model mapping in Postgres.",
                hint = "Run sync_model_mappings to populate from frontmatter.",
            });
        }

        return FormatRecommendation(agentMapping, topAgent.Agent.Name, task, clientTool);
    }

    [McpServerTool(Name = "list_llm_providers")]
    [Description("List all configured LLM providers and their available models. Shows which providers are enabled and what models are accessible.")]
    public string ListLlmProviders()
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "ModelMappingTools.ListLlmProviders",
        });
        logger.LogDebug("ListLlmProviders invoked");

        var providers = options.LlmProviders;
        return JsonSerializer.Serialize(new
        {
            count = providers.Count,
            enabledCount = providers.Count(p => p.Enabled),
            providers = providers.Select(p => new
            {
                p.Name,
                p.Enabled,
                p.ApiBaseUrl,
                hasApiKey = !string.IsNullOrWhiteSpace(p.ApiKey) && p.ApiKey != "${env:}",
                modelCount = p.Models.Count,
                p.Models,
                p.Notes,
            }),
        }, JsonOptions);
    }

    private string FormatRecommendation(AgentModelMapping mapping, string agentName, string? task, string? clientTool)
    {
        var effective = ModelToolOverrideResolver.ResolveEffectiveModel(
            mapping,
            requestedTool: clientTool,
            configuredTool: options.ModelMapping.ActiveClientTool);

        var effectiveProvider = ResolveProvider(effective.EffectiveModel);

        // Check provider availability for recommendations
        var enabledProviders = options.LlmProviders.Where(p => p.Enabled).ToList();
        var primaryAvailable = enabledProviders.Any(p =>
            p.Models.Contains(effective.EffectiveModel, StringComparer.OrdinalIgnoreCase));
        var alt1Available = mapping.AltModel1 is not null && enabledProviders.Any(p =>
            p.Models.Contains(mapping.AltModel1, StringComparer.OrdinalIgnoreCase));
        var alt2Available = mapping.AltModel2 is not null && enabledProviders.Any(p =>
            p.Models.Contains(mapping.AltModel2, StringComparer.OrdinalIgnoreCase));

        return JsonSerializer.Serialize(new
        {
            task,
            agent = agentName,
            recommendation = new
            {
                model = effective.EffectiveModel,
                mapping.Tier,
                provider = effectiveProvider,
                available = primaryAvailable,
                overrideApplied = effective.UsedOverride,
                tool = effective.EffectiveTool,
            },
            alternatives = new[]
            {
                mapping.AltModel1 is not null
                    ? new { model = mapping.AltModel1, provider = mapping.AltProvider1, available = alt1Available }
                    : null,
                mapping.AltModel2 is not null
                    ? new { model = mapping.AltModel2, provider = mapping.AltProvider2, available = alt2Available }
                    : null,
            }.Where(a => a is not null),
            cost = mapping.CostPer1MIn.HasValue || mapping.CostPer1MOut.HasValue
                ? new { inputPer1M = mapping.CostPer1MIn, outputPer1M = mapping.CostPer1MOut }
                : null,
            toolOverrides = ModelToolOverrideResolver.ParseToolOverrides(mapping.ToolOverridesJson),
            mapping.Notes,
            fetch = $"get_agent(\"{agentName}\")",
        }, JsonOptions);
    }

    private string? ResolveProvider(string modelName)
    {
        return options.LlmProviders
            .Where(p => p.Enabled)
            .FirstOrDefault(p => p.Models.Contains(modelName, StringComparer.OrdinalIgnoreCase))
            ?.Name;
    }
}
