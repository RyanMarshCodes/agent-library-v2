namespace Ryan.MCP.Mcp.Services;

/// <summary>
/// Detects agent format based on file name and content structure.
/// </summary>
public static class AgentFormatDetector
{
    /// <summary>Detected format for Claude agents.</summary>
    public const string FormatClaude = "claude";

    /// <summary>Detected format for GitHub Copilot agents.</summary>
    public const string FormatCopilot = "copilot";

    /// <summary>Detected format for skills (usually YAML-heavy).</summary>
    public const string FormatSkill = "skill";

    /// <summary>Detected format for generic prompts/instructions.</summary>
    public const string FormatGeneric = "generic";

    /// <summary>
    /// Detects the agent format based on file name and frontmatter.
    /// </summary>
    public static string DetectFormat(string fileName, Dictionary<string, object> frontmatter)
    {
        var fileNameLower = fileName.ToLowerInvariant();

        if (fileNameLower.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase))
        {
            return DetectAgentTypeFromFrontmatter(frontmatter) ?? FormatClaude;
        }

        if (fileNameLower.EndsWith(".instructions.md", StringComparison.OrdinalIgnoreCase))
        {
            return FormatCopilot;
        }

        if (fileNameLower.EndsWith(".skill.md", StringComparison.OrdinalIgnoreCase))
        {
            return FormatSkill;
        }

        if (fileNameLower.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase))
        {
            return FormatGeneric;
        }

        if (fileNameLower.Equals("copilot-instructions.md", StringComparison.OrdinalIgnoreCase))
        {
            return FormatCopilot;
        }

        return DetectAgentTypeFromFrontmatter(frontmatter) ?? FormatGeneric;
    }

    private static string? DetectAgentTypeFromFrontmatter(Dictionary<string, object> frontmatter)
    {
        if (frontmatter.ContainsKey("instructions") || frontmatter.ContainsKey("model"))
        {
            return FormatClaude;
        }

        if (frontmatter.ContainsKey("copilot") || frontmatter.ContainsKey("vscode-copilot"))
        {
            return FormatCopilot;
        }

        if (frontmatter.ContainsKey("skill"))
        {
            return FormatSkill;
        }

        if (frontmatter.TryGetValue("type", out var typeValue) &&
            (typeValue?.ToString()?.Contains("skill", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return FormatSkill;
        }

        return null;
    }
}
