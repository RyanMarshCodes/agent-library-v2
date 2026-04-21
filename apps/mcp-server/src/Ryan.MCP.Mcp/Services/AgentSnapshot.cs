namespace Ryan.MCP.Mcp.Services;

/// <summary>
/// Represents the complete ingested agent index snapshot.
/// </summary>
public class AgentSnapshot
{
    /// <summary>
    /// Gets an empty snapshot singleton.
    /// </summary>
    public static AgentSnapshot Empty { get; } = new()
    {
        ProjectSlug = string.Empty,
        UpdatedUtc = DateTime.UtcNow,
        LastStartedUtc = DateTime.UtcNow,
        TotalAgents = 0,
        ByScope = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        ByFormat = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        ScanRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Agents = [],
    };

    /// <summary>
    /// Gets or sets the project slug for scope isolation.
    /// </summary>
    public string ProjectSlug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the snapshot was last updated.
    /// </summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the last ingestion started.
    /// </summary>
    public DateTime LastStartedUtc { get; set; }

    /// <summary>
    /// Gets or sets the total number of agents indexed.
    /// </summary>
    public int TotalAgents { get; set; }

    /// <summary>
    /// Gets or sets the count of agents by scope.
    /// </summary>
    public Dictionary<string, int> ByScope { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the count of agents by format (claude, copilot, etc.).
    /// </summary>
    public Dictionary<string, int> ByFormat { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the root directories scanned.
    /// </summary>
    public Dictionary<string, string> ScanRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the list of all ingested agents.
    /// </summary>
    public List<AgentEntry> Agents { get; set; } = [];
}
