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
    public PolicyOptions Policy { get; set; } = new();
    public WorkflowStateOptions WorkflowState { get; set; } = new();
    public ExecutePolicyOptions ExecutePolicy { get; set; } = new();
    public RoutingBudgetOptions RoutingBudget { get; set; } = new();
    public RoutingPolicyOptions RoutingPolicy { get; set; } = new();
    public RetrievalOptions Retrieval { get; set; } = new();
    public PromptCacheOptions PromptCache { get; set; } = new();
    public KnowledgeLibraryOptions KnowledgeLibrary { get; set; } = new();
    public OpenSearchOptions OpenSearch { get; set; } = new();
    public EmbeddingsOptions Embeddings { get; set; } = new();
}

public class PolicyOptions
{
    public bool RequireApprovalTokenForMutate { get; set; } = true;
    public bool RequireApprovalTokenForExecute { get; set; } = true;
    public string? ApprovalToken { get; set; }
    public List<string> ApprovalTokens { get; set; } = [];
    public List<string> ReadToolPrefixes { get; set; } =
    [
        "list_",
        "get_",
        "read_",
        "search_",
        "memory_recall",
        "pr_checks_status"
    ];
    public List<string> MutateToolPrefixes { get; set; } =
    [
        "create_",
        "update_",
        "upsert_",
        "delete_",
        "memory_persist",
        "memory_link",
        "pr_create_or_update",
        "issue_sync",
        "workflow_state_upsert"
    ];
    public List<string> ExecuteToolPrefixes { get; set; } =
    [
        "run_",
        "fix_",
        "call_external_mcp_tool",
        "exec_",
        "shell_"
    ];
}

public class WorkflowStateOptions
{
    /// <summary>
    /// Workflow state backend provider: "postgres" or "inmemory".
    /// </summary>
    public string Provider { get; set; } = "postgres";
}

public class ExecutePolicyOptions
{
    /// <summary>
    /// When true, process execution tools validate command names against AllowedCommands.
    /// </summary>
    public bool EnforceAllowlist { get; set; } = true;

    /// <summary>
    /// Allowed executable names (case-insensitive). Keep this list minimal.
    /// </summary>
    public List<string> AllowedCommands { get; set; } =
    [
        "dotnet",
        "git",
        "gh",
        "npm",
        "pnpm",
        "yarn",
        "bun"
    ];
}

public class RoutingBudgetOptions
{
    /// <summary>
    /// Enables projected cost checks for model routing recommendations.
    /// </summary>
    public bool EnforcePerWorkflowBudget { get; set; } = true;

    /// <summary>
    /// Default USD ceiling for a workflow when no explicit override is provided.
    /// </summary>
    public decimal DefaultWorkflowBudgetUsd { get; set; } = 1.00m;

    /// <summary>
    /// Optional per-workflow budget overrides.
    /// Key examples: "/feature-delivery", "/incident-triage".
    /// </summary>
    public Dictionary<string, decimal> WorkflowBudgetUsd { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class RoutingPolicyOptions
{
    /// <summary>
    /// Prefer small models by default and only escalate for risk/complexity triggers.
    /// </summary>
    public bool EnforceSmallFirst { get; set; } = true;

    /// <summary>
    /// Estimated total tokens that trigger at least capable-tier routing.
    /// </summary>
    public int EscalateAtEstimatedTotalTokens { get; set; } = 6000;

    /// <summary>
    /// If true, try fallback chain when projected budget is exceeded.
    /// </summary>
    public bool EnableBudgetFallback { get; set; } = true;

    /// <summary>
    /// Escalation chain from cheapest to strongest.
    /// </summary>
    public List<string> EscalationChain { get; set; } = ["small", "capable", "frontier"];

    /// <summary>
    /// Fallback chain used when budget is exceeded.
    /// </summary>
    public List<string> FallbackChain { get; set; } = ["frontier", "capable", "small"];

    /// <summary>
    /// Keywords that generally require capable-tier routing.
    /// </summary>
    public List<string> MediumRiskKeywords { get; set; } =
    [
        "refactor",
        "migration",
        "database",
        "performance",
        "distributed",
        "concurrency",
        "auth",
        "security"
    ];

    /// <summary>
    /// Keywords that generally require frontier-tier routing.
    /// </summary>
    public List<string> HighRiskKeywords { get; set; } =
    [
        "incident",
        "outage",
        "sev",
        "rollback",
        "production",
        "compliance",
        "security breach",
        "data loss"
    ];
}

public class RetrievalOptions
{
    /// <summary>
    /// Hard upper bound for knowledge retrieval result count.
    /// </summary>
    public int MaxKnowledgeResults { get; set; } = 20;

    /// <summary>
    /// Hard upper bound for retrieval snippet characters.
    /// </summary>
    public int MaxSnippetChars { get; set; } = 220;

    /// <summary>
    /// Enable in-memory semantic cache for knowledge retrieval responses.
    /// </summary>
    public bool EnableSemanticCache { get; set; } = true;

    /// <summary>
    /// TTL for semantic cache entries.
    /// </summary>
    public int SemanticCacheTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Max number of semantic cache entries retained in-memory.
    /// </summary>
    public int SemanticCacheMaxEntries { get; set; } = 500;
}

public class PromptCacheOptions
{
    /// <summary>
    /// Enable in-memory stable prefix caching for workflow and prompt templates.
    /// </summary>
    public bool EnablePrefixCache { get; set; } = true;

    /// <summary>
    /// TTL for prompt prefix cache entries.
    /// </summary>
    public int PrefixCacheTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Max number of cached prompt prefix entries.
    /// </summary>
    public int PrefixCacheMaxEntries { get; set; } = 1000;
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

public class KnowledgeLibraryOptions
{
    public bool Enabled { get; set; } = false;
    public string KnowledgePath { get; set; } = "/data/knowledge-library/knowledge";
    public string RawPendingPath { get; set; } = "/data/knowledge-library/raw/pending";
    public string RawProcessedPath { get; set; } = "/data/knowledge-library/raw/processed";
}

public class OpenSearchOptions
{
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "http://localhost:9200";
    public string IndexName { get; set; } = "knowledge-wiki";
    public string PipelineName { get; set; } = "knowledge-hybrid-pipeline";
    public int KnnCandidates { get; set; } = 20;
}

public class EmbeddingsOptions
{
    public bool Enabled { get; set; } = false;
    public string Provider { get; set; } = "openai";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "text-embedding-3-small";
    public int Dimensions { get; set; } = 1536;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
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
