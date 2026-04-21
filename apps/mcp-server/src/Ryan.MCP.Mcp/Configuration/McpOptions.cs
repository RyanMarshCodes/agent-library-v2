namespace Ryan.MCP.Mcp.Configuration;

public class McpOptions
{
    public const string SectionName = "McpOptions";

    public KnowledgeOptions Knowledge { get; set; } = new();
    public IngestionOptions Ingestion { get; set; } = new();
    public AgentOptions? Agents { get; set; }
    public MemoryStoreOptions MemoryStore { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public List<ExternalMcpConnectorOptions> ExternalConnectors { get; set; } = [];
    public Dictionary<string, string> ExternalConnectorEndpointOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ExternalMcpOptions ExternalMcp { get; set; } = new();
    public List<LlmProviderOptions> LlmProviders { get; set; } = [];
    public ModelMappingOptions ModelMapping { get; set; } = new();
}

public class ExternalMcpOptions
{
    /// <summary>
    /// Protocol version to request when creating outbound MCP clients.
    /// Keep configurable because remote MCP servers may lag SDK defaults.
    /// </summary>
    public string ProtocolVersion { get; set; } = "2025-06-18";
}

public class ModelMappingOptions
{
    /// <summary>
    /// Optional default client tool used when resolving model overrides by tool.
    /// Examples: opencode, copilot, claude, cursor, gemini.
    /// </summary>
    public string? ActiveClientTool { get; set; }
}

/// <summary>
/// An LLM provider the user has access to (e.g., OpenCode Zen, Anthropic, OpenAI, Google).
/// Used by model recommendation tools to filter suggestions to models the user can actually reach.
/// </summary>
public class LlmProviderOptions
{
    /// <summary>Unique provider slug (e.g., "opencode-zen", "anthropic", "openai", "google").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this provider is currently active/usable.</summary>
    public bool Enabled { get; set; }

    /// <summary>Optional API base URL. Not used for routing — purely informational / future use.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// API key with ${env:VAR} expansion support. Never stored in plain text — use env var references.
    /// Not used for LLM calls (Ryan.MCP is advisory-only), but used for quota/usage API checks when available.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Models available through this provider (e.g., ["claude-opus-4-6", "claude-sonnet-4-6"]).</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>Optional notes (e.g., "pay-as-you-go", "free tier", "subscription").</summary>
    public string? Notes { get; set; }
}

public class MemoryStoreOptions
{
    public string Provider { get; set; } = "postgres";
    public string ConnectionString { get; set; } = "Host=localhost;Port=8810;Database=ryan_mcp;Username=postgres;Password=postgres";
    public int CommandTimeoutSeconds { get; set; } = 30;
}

public class KnowledgeOptions
{
    public string ProjectSlug { get; set; } = "ryan-mcp";

    public string? OfficialPath { get; set; }

    public string? OrganizationPath { get; set; }

    public string? ProjectPath { get; set; }

    public string? IndexPath { get; set; }
}

public class IngestionOptions
{
    public bool AutoIngestOnStartup { get; set; } = true;
    public bool WatchForChanges { get; set; } = true;
    public int DebounceMilliseconds { get; set; } = 1500;
    public List<string> AllowedExtensions { get; set; } = [".md", ".txt", ".json", ".yaml", ".yml"];
}

public class AgentOptions
{
    public string? LocalPath { get; set; }

    public string? LibraryPath { get; set; }

    public string? ProjectPath { get; set; }
}

public class StorageOptions
{
    public string Provider { get; set; } = "file"; // "file" or "azure"
    public string? ConnectionString { get; set; }
    public string ContainerName { get; set; } = "mcp-storage";
}

public class ExternalMcpConnectorOptions
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Transport { get; set; } = "http";
    public string Endpoint { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = 8000;
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
