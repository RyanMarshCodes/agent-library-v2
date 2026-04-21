namespace Ryan.MCP.Mcp.Services.ModelMapping;

/// <summary>
/// A persisted agent-to-model mapping with tier, provider, and cost metadata.
/// </summary>
public record AgentModelMapping(
    string AgentName,
    string Tier,
    string PrimaryModel,
    string? PrimaryProvider = null,
    string? ToolOverridesJson = null,
    string? AltModel1 = null,
    string? AltProvider1 = null,
    string? AltModel2 = null,
    string? AltProvider2 = null,
    decimal? CostPer1MIn = null,
    decimal? CostPer1MOut = null,
    string? Notes = null,
    string SyncedFrom = "frontmatter");
