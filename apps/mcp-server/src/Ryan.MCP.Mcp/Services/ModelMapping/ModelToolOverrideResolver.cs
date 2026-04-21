using System.Text.Json;

namespace Ryan.MCP.Mcp.Services.ModelMapping;

public static class ModelToolOverrideResolver
{
    public static Dictionary<string, string> ParseToolOverrides(string? toolOverridesJson)
    {
        if (string.IsNullOrWhiteSpace(toolOverridesJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(toolOverridesJson);
            if (parsed is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return parsed
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                .ToDictionary(
                    kvp => NormalizeToolName(kvp.Key),
                    kvp => kvp.Value.Trim(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string NormalizeToolName(string toolName)
    {
        return toolName
            .Trim()
            .ToLowerInvariant()
            .Replace('_', '-')
            .Replace(' ', '-');
    }

    public static (string EffectiveModel, string? EffectiveTool, bool UsedOverride) ResolveEffectiveModel(
        AgentModelMapping mapping,
        string? requestedTool,
        string? configuredTool)
    {
        var targetTool = !string.IsNullOrWhiteSpace(requestedTool) ? requestedTool : configuredTool;
        if (string.IsNullOrWhiteSpace(targetTool))
        {
            return (mapping.PrimaryModel, null, false);
        }

        var normalizedTool = NormalizeToolName(targetTool);
        var overrides = ParseToolOverrides(mapping.ToolOverridesJson);

        return overrides.TryGetValue(normalizedTool, out var overrideModel)
            ? (overrideModel, normalizedTool, true)
            : (mapping.PrimaryModel, normalizedTool, false);
    }
}
