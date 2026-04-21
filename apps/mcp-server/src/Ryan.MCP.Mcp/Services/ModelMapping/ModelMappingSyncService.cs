using System.Text.RegularExpressions;
using System.Text.Json;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.ModelMapping;

/// <summary>
/// Syncs model mappings from agent frontmatter into Postgres.
/// Parses the format: model: primary-model # tier — alt: alt1, alt2
/// Optional tool overrides: model_by_tool: opencode=modelA, copilot=modelB
/// </summary>
public sealed partial class ModelMappingSyncService(
    AgentIngestionCoordinator agents,
    IModelMappingStore store,
    McpOptions options,
    ILogger<ModelMappingSyncService> logger)
{
    // Matches: model: <primary> # <tier> — alt: <alt1>, <alt2>
    // Also handles: model: <primary> # <tier> -- alt: <alt1>, <alt2>  (ASCII dashes)
    // Groups: 1=primary, 2=tier, 3=alt-list (optional)
    [GeneratedRegex(
        @"^model:\s*(\S+)\s*#\s*(\S+)\s*(?:[—–-]{1,2}\s*alt:\s*(.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ModelLineRegex();

    // Matches: model_by_tool: tool1=modelA, tool2=modelB
    [GeneratedRegex(
        @"^(?:model_by_tool|models_by_tool):\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ModelByToolLineRegex();

    // Matches a YAML frontmatter closing delimiter: "---" at the start of a line
    [GeneratedRegex(@"^---\s*$", RegexOptions.Multiline)]
    private static partial Regex FrontmatterDelimiterRegex();

    /// <summary>
    /// Syncs all agent frontmatter model fields into the agent_model_mappings table.
    /// Manual overrides (synced_from = 'manual') are preserved by default.
    /// </summary>
    public async Task<SyncResult> SyncAsync(bool preserveManual = true, CancellationToken ct = default)
    {
        var snapshot = agents.Snapshot;
        if (snapshot.TotalAgents == 0)
        {
            logger.LogWarning("No agents indexed; skipping model mapping sync");
            return new SyncResult(0, 0, 0);
        }

        var mappings = new List<AgentModelMapping>();
        var parseErrors = 0;

        foreach (var agent in snapshot.Agents)
        {
            var mapping = ParseModelFromRawContent(agent);
            if (mapping is not null)
            {
                mappings.Add(mapping);
            }
            else if (agent.Frontmatter.TryGetValue("model", out var modelVal) && modelVal is string)
            {
                // Frontmatter has model but we couldn't parse the comment format — use bare value
                var toolOverrides = ParseModelByToolOverrides(agent.RawContent ?? string.Empty);
                var toolOverridesJson = toolOverrides.Count > 0 ? JsonSerializer.Serialize(toolOverrides) : null;
                mappings.Add(new AgentModelMapping(
                    AgentName: agent.Name,
                    Tier: "unknown",
                    PrimaryModel: modelVal.ToString()!.Trim(),
                    ToolOverridesJson: toolOverridesJson,
                    SyncedFrom: "frontmatter"));
                parseErrors++;
            }
        }

        if (mappings.Count > 0)
        {
            await store.BulkUpsertAsync(mappings, preserveManual, ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Model mapping sync complete: {Total} agents, {Synced} mapped, {Errors} partial (no tier/alts)",
            snapshot.TotalAgents, mappings.Count, parseErrors);

        return new SyncResult(mappings.Count, parseErrors, snapshot.TotalAgents - mappings.Count);
    }

    /// <summary>
    /// Parse the model line from an agent's raw Markdown content.
    /// Returns null if no model field found.
    /// </summary>
    internal AgentModelMapping? ParseModelFromRawContent(AgentEntry agent)
    {
        // Search the frontmatter section of the raw content for the model line
        var content = agent.RawContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // Find frontmatter delimiters (must be "---" at the start of a line)
        // Using regex to avoid false matches on "---" inside YAML comments
        var delimMatches = FrontmatterDelimiterRegex().Matches(content);
        if (delimMatches.Count < 2)
        {
            return null;
        }

        var firstDelim = delimMatches[0];
        var secondDelim = delimMatches[1];
        var frontmatterBlock = content[(firstDelim.Index + firstDelim.Length)..secondDelim.Index];

        var match = ModelLineRegex().Match(frontmatterBlock);
        if (!match.Success)
        {
            return null;
        }

        var primaryModel = match.Groups[1].Value.Trim();
        // Normalize tier: replace "/" with "-" (e.g. "strong/coding" → "strong-coding")
        var tier = match.Groups[2].Value.Trim().Replace('/', '-');
        string? alt1 = null, alt2 = null;

        if (match.Groups[3].Success)
        {
            var alts = match.Groups[3].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (alts.Length > 0)
            {
                alt1 = alts[0];
            }

            if (alts.Length > 1)
            {
                alt2 = alts[1];
            }
        }

        // Resolve providers from LlmProviders config
        var primaryProvider = ResolveProvider(primaryModel);
        var altProvider1 = alt1 is not null ? ResolveProvider(alt1) : null;
        var altProvider2 = alt2 is not null ? ResolveProvider(alt2) : null;
        var toolOverrides = ParseModelByToolOverrides(frontmatterBlock);
        var toolOverridesJson = toolOverrides.Count > 0 ? JsonSerializer.Serialize(toolOverrides) : null;

        return new AgentModelMapping(
            AgentName: agent.Name,
            Tier: tier,
            PrimaryModel: primaryModel,
            PrimaryProvider: primaryProvider,
            ToolOverridesJson: toolOverridesJson,
            AltModel1: alt1,
            AltProvider1: altProvider1,
            AltModel2: alt2,
            AltProvider2: altProvider2,
            SyncedFrom: "frontmatter");
    }

    /// <summary>
    /// Find the first enabled provider that lists the given model.
    /// </summary>
    private string? ResolveProvider(string modelName)
    {
        return options.LlmProviders
            .Where(p => p.Enabled)
            .FirstOrDefault(p => p.Models.Contains(modelName, StringComparer.OrdinalIgnoreCase))
            ?.Name;
    }

    private static Dictionary<string, string> ParseModelByToolOverrides(string frontmatterBlock)
    {
        var match = ModelByToolLineRegex().Match(frontmatterBlock);
        if (!match.Success)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var raw = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                idx = pair.IndexOf(':');
            }

            if (idx <= 0 || idx >= pair.Length - 1)
            {
                continue;
            }

            var tool = ModelToolOverrideResolver.NormalizeToolName(pair[..idx]);
            var model = pair[(idx + 1)..].Trim().Trim('"', '\'');

            if (!string.IsNullOrWhiteSpace(tool) && !string.IsNullOrWhiteSpace(model))
            {
                result[tool] = model;
            }
        }

        return result;
    }
}

public record SyncResult(int Synced, int PartiallyParsed, int Skipped);
