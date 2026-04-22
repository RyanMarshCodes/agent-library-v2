using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Configuration;
using Ryan.MCP.Mcp.Services;
using Ryan.MCP.Mcp.Services.Knowledge;
using Ryan.MCP.Mcp.Services.Memory;
using Ryan.MCP.Mcp.Services.Observability;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class DocumentTools(
    DocumentIngestionCoordinator documents,
    IMemoryStore memoryStore,
    SemanticRetrievalCache retrievalCache,
    PlatformMetrics metrics,
    McpOptions options,
    ILogger<DocumentTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private const int SearchDocumentsResultLimit = 20;

    [McpServerTool(Name = "list_standards")]
    [Description("List all indexed standards and knowledge documents by tier (official, organization, project). Returns relative file paths. Use read_document to fetch content.")]
    public string ListStandards(
        [Description("Filter by tier: 'official', 'organization', or 'project'. Omit to list all.")] string? tier = null,
        [Description("Filter by language or subdirectory prefix (e.g. 'csharp', 'typescript'). Omit to list all.")] string? language = null)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "DocumentTools.ListStandards",
            ["Tier"] = tier,
            ["Language"] = language,
        });
        logger.LogDebug("ListStandards invoked");

        var snapshot = documents.Snapshot;
        var docs = snapshot.Documents.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(tier))
            docs = docs.Where(d => d.Tier.Equals(tier, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(language))
            docs = docs.Where(d => d.RelativePath.StartsWith(language + "/", StringComparison.OrdinalIgnoreCase)
                                || d.RelativePath.StartsWith(language + "\\", StringComparison.OrdinalIgnoreCase));

        var results = docs.ToList();
        var byTier = results
            .GroupBy(d => d.Tier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(d => d.RelativePath).OrderBy(p => p).ToList());

        return JsonSerializer.Serialize(new
        {
            snapshot.ProjectSlug,
            totalDocuments = results.Count,
            snapshot.ByTier,
            snapshot.Roots,
            filter = new { tier, language },
            documents = byTier,
        }, JsonOptions);
    }

    [McpServerTool(Name = "read_document")]
    [Description("Read the full content of a standards document by tier and relative path. Use list_standards or documents://list to discover available paths.")]
    public async Task<string> ReadDocument(
        [Description("Document tier: 'official', 'organization', or 'project'")] string tier,
        [Description("Relative path as returned by list_standards, e.g. 'csharp/async-programming.md'")] string path,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "DocumentTools.ReadDocument",
            ["Tier"] = tier,
            ["Path"] = path,
        });
        logger.LogDebug("ReadDocument invoked");

        var entry = documents.Snapshot.Documents.FirstOrDefault(d =>
            d.Tier.Equals(tier, StringComparison.OrdinalIgnoreCase) &&
            d.RelativePath.Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Document '{path}' not found in tier '{tier}'.",
                hint = "Use list_standards to see available documents and their exact paths.",
            });
        }

        try
        {
            var content = await File.ReadAllTextAsync(entry.AbsolutePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                tier = entry.Tier,
                path = entry.RelativePath,
                sizeBytes = entry.SizeBytes,
                lastWriteUtc = entry.LastWriteUtc,
                content,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to read document: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "search_documents")]
    [Description("Search standards documents by keyword across file paths and content. Returns matching documents with the tier and path needed to read them.")]
    public async Task<string> SearchDocuments(
        [Description("Search query, e.g. 'async await' or 'dependency injection' or 'error handling'")] string query,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "DocumentTools.SearchDocuments",
            ["Query"] = query,
        });
        logger.LogDebug("SearchDocuments invoked");

        if (string.IsNullOrWhiteSpace(query))
            return JsonSerializer.Serialize(new { error = "query is required" });

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<object>();

        foreach (var doc in documents.Snapshot.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= SearchDocumentsResultLimit)
                break;

            var pathMatch = terms.Any(t => doc.RelativePath.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (pathMatch)
            {
                results.Add(new { doc.Tier, doc.RelativePath, matchedOn = "path", readWith = $"read_document(\"{doc.Tier}\", \"{doc.RelativePath}\")" });
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(doc.AbsolutePath, cancellationToken).ConfigureAwait(false);
                if (terms.Any(t => content.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    results.Add(new { doc.Tier, doc.RelativePath, matchedOn = "content", readWith = $"read_document(\"{doc.Tier}\", \"{doc.RelativePath}\")" });
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skipping unreadable document during search: {Path}", doc.AbsolutePath);
            }
        }

        return JsonSerializer.Serialize(new { query, count = results.Count, results }, JsonOptions);
    }

    [McpServerTool(Name = "knowledge_retrieve")]
    [Description("Unified retrieval across standards documents and long-term memory. Use scope='all' for default behavior, or 'documents'/'memory' for targeted recall.")]
    public async Task<string> KnowledgeRetrieve(
        [Description("Query text to retrieve relevant knowledge.")] string query,
        [Description("Retrieval scope: 'all', 'documents', or 'memory'.")] string scope = "all",
        [Description("Maximum results (default 8, min 1, max 20).")] int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        using var scopeLog = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "DocumentTools.KnowledgeRetrieve",
            ["Query"] = query,
            ["Scope"] = scope,
            ["MaxResults"] = maxResults,
        });

        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(new { error = "query is required" }, JsonOptions);
        }

        maxResults = Math.Clamp(maxResults, 1, Math.Max(1, options.Retrieval.MaxKnowledgeResults));
        var normalizedScope = (scope ?? "all").Trim().ToLowerInvariant();
        if (normalizedScope is not ("all" or "documents" or "memory"))
        {
            return JsonSerializer.Serialize(new { error = "scope must be one of: all, documents, memory" }, JsonOptions);
        }

        var cacheKey = BuildRetrievalCacheKey(query, normalizedScope, maxResults);
        if (retrievalCache.TryGet(cacheKey, out var cached))
        {
            metrics.RecordKnowledgeCacheHit();
            return cached;
        }

        metrics.RecordKnowledgeCacheMiss();

        var hits = new List<RetrievalHit>();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (normalizedScope is "all" or "documents")
        {
            var documentHits = await RetrieveDocumentHitsAsync(terms, maxResults, cancellationToken).ConfigureAwait(false);
            hits.AddRange(documentHits.Select(h => new RetrievalHit(
                Source: "documents",
                Id: h.Id,
                Score: h.Score,
                Snippet: h.Snippet,
                Citation: h.Citation)));
        }

        if (normalizedScope is "all" or "memory")
        {
            var memoryHits = await memoryStore.SearchAsync(query, maxResults, cancellationToken).ConfigureAwait(false);
            hits.AddRange(memoryHits.Select(entity => new RetrievalHit(
                Source: "memory",
                Id: entity.Name,
                Score: 0.85,
                Snippet: entity.Observations.FirstOrDefault() ?? entity.Name,
                Citation: $"memory:{entity.EntityType}/{entity.Name}")));
        }

        var ordered = hits
            .OrderByDescending(h => h.Score)
            .Take(maxResults)
            .ToList();

        var response = JsonSerializer.Serialize(new
        {
            query,
            scope = normalizedScope,
            count = ordered.Count,
            results = ordered,
            cache = new
            {
                enabled = options.Retrieval.EnableSemanticCache,
                keyScope = cacheKey,
                ttlMinutes = options.Retrieval.SemanticCacheTtlMinutes
            },
            hint = "Use read_document for full standards content or memory_read for full graph when needed.",
        }, JsonOptions);

        retrievalCache.Set(cacheKey, response);
        return response;
    }

    [McpServerTool(Name = "ingestion_status")]
    [Description("Get the current document ingestion status — when documents were last indexed and how many are indexed per tier.")]
    public string IngestionStatus()
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "DocumentTools.IngestionStatus",
        });
        logger.LogDebug("IngestionStatus invoked");

        var snapshot = documents.Snapshot;
        return JsonSerializer.Serialize(new
        {
            snapshot.ProjectSlug,
            snapshot.LastStartedUtc,
            snapshot.UpdatedUtc,
            snapshot.TotalDocuments,
            snapshot.ByTier,
            snapshot.Roots,
        }, JsonOptions);
    }

    [McpServerTool(Name = "knowledge_status")]
    [Description("Get unified knowledge system status: document ingestion freshness and memory backend availability.")]
    public async Task<string> KnowledgeStatus(CancellationToken cancellationToken = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "DocumentTools.KnowledgeStatus",
        });
        logger.LogDebug("KnowledgeStatus invoked");

        var snapshot = documents.Snapshot;
        var (memoryAvailable, memoryMessage) = await memoryStore.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        var roots = snapshot.Roots
            .Select(r => new
            {
                tier = r.Key,
                path = r.Value,
                exists = Directory.Exists(r.Value),
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            status = memoryAvailable ? "ok" : "degraded",
            projectSlug = snapshot.ProjectSlug,
            ingestion = new
            {
                snapshot.LastStartedUtc,
                snapshot.UpdatedUtc,
                snapshot.TotalDocuments,
                byTier = snapshot.ByTier,
                roots,
            },
            memory = new
            {
                available = memoryAvailable,
                backend = "postgres",
                message = memoryMessage,
            },
        }, JsonOptions);
    }

    [McpServerTool(Name = "ingest_documents")]
    [Description("Trigger a re-scan and re-index of all knowledge documents. Use after adding or modifying documents.")]
    public async Task<string> IngestDocuments(CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "DocumentTools.IngestDocuments",
        });
        logger.LogDebug("IngestDocuments invoked");

        await documents.TriggerReindexAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = documents.Snapshot;
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            snapshot.ProjectSlug,
            snapshot.UpdatedUtc,
            snapshot.TotalDocuments,
            snapshot.ByTier,
        }, JsonOptions);
    }

    private async Task<List<DocumentHit>> RetrieveDocumentHitsAsync(
        string[] terms,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var hits = new List<DocumentHit>();
        foreach (var doc in documents.Snapshot.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count >= maxResults)
                break;

            var pathScore = terms.Count(t => doc.RelativePath.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (pathScore > 0)
            {
                hits.Add(new DocumentHit(
                    Id: $"{doc.Tier}:{doc.RelativePath}",
                    Score: Math.Min(1.0, 0.6 + (pathScore * 0.1)),
                    Snippet: doc.RelativePath,
                    Citation: $"documents:{doc.Tier}/{doc.RelativePath}"));
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(doc.AbsolutePath, cancellationToken).ConfigureAwait(false);
                var matchedTerm = terms.FirstOrDefault(t => content.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (matchedTerm is null)
                    continue;

                var index = content.IndexOf(matchedTerm, StringComparison.OrdinalIgnoreCase);
                var start = Math.Max(0, index - 80);
                var length = Math.Min(Math.Max(50, options.Retrieval.MaxSnippetChars), content.Length - start);
                var snippet = content.Substring(start, length).Replace('\n', ' ').Replace('\r', ' ').Trim();

                hits.Add(new DocumentHit(
                    Id: $"{doc.Tier}:{doc.RelativePath}",
                    Score: 0.72,
                    Snippet: snippet,
                    Citation: $"documents:{doc.Tier}/{doc.RelativePath}"));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skipping unreadable document during unified retrieval: {Path}", doc.AbsolutePath);
            }
        }

        return hits;
    }

    private string BuildRetrievalCacheKey(string query, string scope, int maxResults)
    {
        // Include ingestion freshness so doc changes naturally invalidate retrieval cache.
        var snapshot = documents.Snapshot;
        var docVersion = snapshot.UpdatedUtc.ToString("O");
        var normalizedQuery = query.Trim().ToLowerInvariant();
        return $"v1|{options.Knowledge.ProjectSlug}|{scope}|{maxResults}|{docVersion}|{normalizedQuery}";
    }

    private sealed record DocumentHit(string Id, double Score, string Snippet, string Citation);
    private sealed record RetrievalHit(string Source, string Id, double Score, string Snippet, string Citation);
}
