using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services.Memory;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class MemoryTools(
    IMemoryStore memoryStore,
    ILogger<MemoryTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "memory_recall")]
    [Description(
        "Query the knowledge graph for relevant context. Use this to recall past decisions, " +
        "architecture notes, team conventions, or project-specific details. " +
        "Efficient: returns top 5 most relevant results to minimize token usage. " +
        "Only call this when the task involves: architecture decisions, past discussions, " +
        "team conventions, or references to previous work. Do NOT call at session start " +
        "unless you have a specific reason to recall past context.")]
    public async Task<string> MemoryRecall(
        [Description("Search query, e.g. 'authentication patterns', 'API design decisions', 'team conventions'")] string query,
        [Description("Maximum results to return (default 5, max 10). Lower = fewer tokens.")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 10);

        using (logger.BeginScope(new Dictionary<string, object?> { ["ToolName"] = "MemoryTools.MemoryRecall", ["Query"] = query, ["MaxResults"] = maxResults }))
        {
            logger.LogDebug("MemoryRecall invoked for query={Query}", query);

            try
            {
                var results = await memoryStore.SearchAsync(query, maxResults, cancellationToken).ConfigureAwait(false);
                if (results.Count > 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        source = "memory.postgres.search",
                        query,
                        count = results.Count,
                        confidence = "high",
                        results = results.Select(r => new { name = r.Name, entityType = r.EntityType, observations = r.Observations.Take(3) }),
                    }, JsonOptions);
                }

                return JsonSerializer.Serialize(new
                {
                    status = "empty",
                    source = "memory.postgres.search",
                    query,
                    message = "No existing memory found for this query.",
                    hint = "This is normal for new projects or first sessions.",
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Memory recall failed");
                return ErrorResponse(
                    status: "connector_unavailable",
                    stage: "recall:exception",
                    message: ex.Message,
                    hint: "Check postgres memory backend health.");
            }
        }
    }

    [McpServerTool(Name = "memory_read")]
    [Description(
        "Read the entire knowledge graph. Only use when you need to review ALL stored context. " +
        "Prefer memory_recall for specific queries. This is expensive in tokens.")]
    public async Task<string> MemoryRead(CancellationToken cancellationToken = default)
    {
        try
        {
            var graph = await memoryStore.ReadGraphAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                entities = graph.Entities.Select(e => new
                {
                    name = e.Name,
                    entityType = e.EntityType,
                    observations = e.Observations
                }),
                relations = graph.Relations.Select(r => new
                {
                    from = r.From,
                    to = r.To,
                    relationType = r.RelationType
                }),
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory read failed");
            return ErrorResponse(
                status: "connector_unavailable",
                stage: "read:exception",
                message: ex.Message,
                hint: "Check postgres memory backend.");
        }
    }

    [McpServerTool(Name = "memory_persist")]
    [Description(
        "Store important information in the knowledge graph for future sessions. " +
        "Use for: architecture decisions and their rationale, team conventions discovered, " +
        "patterns that worked or didn't work, project-specific details. " +
        "Do NOT store: secrets, credentials, session-specific context, or temporary notes.")]
    public async Task<string> MemoryPersist(
        [Description("Entity name: use kebab-case, e.g. 'auth-service', 'api-design', 'team-convention'")] string entityName,
        [Description("Entity type: 'concept', 'project', 'pattern', 'decision', 'convention', 'service'")] string entityType,
        [Description("Observations: list of facts to store (keep concise, 1-3 sentences each)")] List<string> observations,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            return ErrorResponse(
                status: "invalid_request",
                stage: "persist:validation",
                message: "entityName is required",
                hint: "Use kebab-case names like 'auth-service'.");
        }

        if (string.IsNullOrWhiteSpace(entityType))
        {
            entityType = "concept";
        }

        if (observations == null || observations.Count == 0)
        {
            return ErrorResponse(
                status: "invalid_request",
                stage: "persist:validation",
                message: "At least one observation is required",
                hint: "Provide concise durable facts, not session chatter.");
        }

        try
        {
            await memoryStore.UpsertEntityAsync(entityName, entityType, observations, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                status = "saved",
                entity = entityName,
                observations = observations.Count,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory persist failed for {Entity}", entityName);
            return ErrorResponse(
                status: "connector_unavailable",
                stage: "persist:exception",
                message: ex.Message,
                hint: "Check postgres memory backend and retry.");
        }
    }

    [McpServerTool(Name = "memory_link")]
    [Description(
        "Create a relationship between two existing entities in the knowledge graph. " +
        "Use to link related concepts, services, patterns, etc.")]
    public async Task<string> MemoryLink(
        [Description("Source entity name (from existing memory)")] string fromEntity,
        [Description("Target entity name (from existing memory)")] string toEntity,
        [Description("Relationship type: 'depends-on', 'relates-to', 'implements', 'uses', 'belongs-to'")] string relationType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fromEntity) || string.IsNullOrWhiteSpace(toEntity))
        {
            return ErrorResponse(
                status: "invalid_request",
                stage: "link:validation",
                message: "fromEntity and toEntity are required",
                hint: "Pass existing entity names for both endpoints.");
        }

        relationType ??= "relates-to";

        try
        {
            await memoryStore.CreateRelationAsync(fromEntity, toEntity, relationType, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                status = "saved",
                relation = new { from = fromEntity, to = toEntity, relationType },
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory link failed from {From} to {To}", fromEntity, toEntity);
            return ErrorResponse(
                status: "connector_unavailable",
                stage: "link:exception",
                message: ex.Message,
                hint: "Check postgres memory backend health.");
        }
    }

    [McpServerTool(Name = "memory_status")]
    [Description("Check memory connector status and get usage hints for token efficiency.")]
    public async Task<string> MemoryStatus(CancellationToken cancellationToken = default)
    {
        try
        {
            var (available, message) = await memoryStore.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            if (!available)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    backend = "postgres",
                    message = "Memory backend is not available.",
                    lastError = message,
                }, JsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                available = true,
                backend = "postgres",
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory status check failed");
            return ErrorResponse(
                status: "connector_unavailable",
                stage: "status:exception",
                message: ex.Message,
                hint: "Check postgres memory backend.");
        }
    }

    private static string ErrorResponse(string status, string stage, string message, string hint)
        => JsonSerializer.Serialize(new
        {
            status,
            errorCode = "memory_operation_failed",
            stage,
            message,
            hint,
        }, JsonOptions);
}