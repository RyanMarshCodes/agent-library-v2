namespace Ryan.MCP.Mcp.Services;

/// <summary>
/// Represents an ingested agent definition with metadata and raw content.
/// </summary>
public class AgentEntry
{
    /// <summary>
    /// Gets or sets the file name (e.g., "code-simplifier.agent.md").
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the agent name (from frontmatter).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the agent description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the scope (e.g., "refactoring", "security-audit").
    /// </summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tags for discovery.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets the detected agent format (claude, copilot, generic, skill).
    /// </summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw file content (full Markdown).
    /// </summary>
    public string RawContent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parsed YAML frontmatter.
    /// </summary>
    public Dictionary<string, object> Frontmatter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the absolute file path.
    /// </summary>
    public string AbsolutePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the relative path from the scan root (e.g., "backend/csharp-expert.agent.md").
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SHA256 checksum for change detection.
    /// </summary>
    public string ChecksumSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp when indexed.
    /// </summary>
    public DateTime IndexedUtc { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the file last write time (UTC).
    /// </summary>
    public DateTime LastWriteUtc { get; set; }
}
