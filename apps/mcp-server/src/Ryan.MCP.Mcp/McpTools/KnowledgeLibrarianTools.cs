using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Configuration;
using Ryan.MCP.Mcp.Services.Knowledge;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class KnowledgeLibrarianTools(
    OpenSearchService openSearch,
    EmbeddingsService embeddings,
    KnowledgeFileService files,
    McpOptions options,
    ILogger<KnowledgeLibrarianTools> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    [McpServerTool(Name = "knowledge_search")]
    [Description("Hybrid BM25 + semantic search over the knowledge library. Returns ranked pages with snippets. Use knowledge_retrieve to read the full content of a result.")]
    public async Task<string> KnowledgeSearch(
        [Description("Search query — natural language or keywords")] string query,
        [Description("Filter by domain prefix, e.g. 'engineering/backend' or 'ai/prompting'. Omit for all domains.")] string? domain = null,
        [Description("Filter by tags, comma-separated, e.g. 'csharp,async'. Omit for no tag filter.")] string? tags = null,
        [Description("Maximum results (1–20, default 8)")] int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        if (!options.OpenSearch.Enabled)
            return JsonSerializer.Serialize(new { error = "Knowledge library search is not enabled. Set McpOptions__OpenSearch__Enabled=true." });

        if (string.IsNullOrWhiteSpace(query))
            return JsonSerializer.Serialize(new { error = "query is required" });

        maxResults = Math.Clamp(maxResults, 1, 20);
        var tagArray = string.IsNullOrWhiteSpace(tags)
            ? null
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var embedding = await embeddings.GetEmbeddingAsync(query, cancellationToken).ConfigureAwait(false);
        var results = await openSearch.SearchAsync(query, embedding, maxResults, domain, tagArray, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            query,
            domain,
            tags = tagArray,
            count = results.Count,
            semantic = embedding is not null,
            results = results.Select(r => new
            {
                r.KnowledgePath,
                r.Title,
                r.Domain,
                r.Tags,
                r.Snippet,
                r.Score,
                r.Confidence,
                updated = r.Updated.ToString("yyyy-MM-dd"),
                readWith = $"knowledge_retrieve(\"{r.KnowledgePath}\")",
            }),
        }, JsonOpts);
    }

    [McpServerTool(Name = "knowledge_retrieve")]
    [Description("Read the full content of a knowledge page by its path, e.g. 'engineering/backend/csharp/async-programming.md'.")]
    public async Task<string> KnowledgeRetrieve(
        [Description("Relative path to the knowledge page, as returned by knowledge_search")] string path,
        CancellationToken cancellationToken = default)
    {
        if (!options.KnowledgeLibrary.Enabled)
            return JsonSerializer.Serialize(new { error = "Knowledge library is not enabled." });

        if (string.IsNullOrWhiteSpace(path))
            return JsonSerializer.Serialize(new { error = "path is required" });

        var content = await files.ReadPageAsync(path, cancellationToken).ConfigureAwait(false);
        if (content is null)
            return JsonSerializer.Serialize(new { error = $"Page '{path}' not found.", hint = "Use knowledge_search to find valid paths." });

        return JsonSerializer.Serialize(new { path, content }, JsonOpts);
    }

    [McpServerTool(Name = "knowledge_store")]
    [Description("Write a knowledge page to the library and index it for search. Use this to store synthesized knowledge, answers, or imported content. The Librarian uses this during ingest.")]
    public async Task<string> KnowledgeStore(
        [Description("Page title")] string title,
        [Description("Full markdown content of the page (without frontmatter — that is added automatically)")] string content,
        [Description("Domain path, e.g. 'engineering/backend/csharp' or 'ai/prompting'")] string domain,
        [Description("Relative path within knowledge/, e.g. 'engineering/backend/csharp/async-tips.md'")] string knowledgePath,
        [Description("Tags, comma-separated")] string? tags = null,
        [Description("Confidence level: high, medium, low, or unverified (default: medium)")] string confidence = "medium",
        [Description("Source file reference, e.g. 'sources/some-article.md'")] string? sourceFile = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.KnowledgeLibrary.Enabled)
            return JsonSerializer.Serialize(new { error = "Knowledge library is not enabled." });

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(knowledgePath))
            return JsonSerializer.Serialize(new { error = "title, content, and knowledgePath are required" });

        var tagArray = string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var validConfidence = confidence is "high" or "medium" or "low" or "unverified" ? confidence : "medium";

        await files.WritePageAsync(knowledgePath, title, content, domain, tagArray, validConfidence, sourceFile, cancellationToken).ConfigureAwait(false);

        var embedding = await embeddings.GetEmbeddingAsync($"{title}\n\n{content}", cancellationToken).ConfigureAwait(false);
        var doc = new KnowledgeDocument
        {
            Title         = title,
            Content       = content,
            Domain        = domain,
            Tags          = tagArray,
            KnowledgePath = knowledgePath,
            SourceFile    = sourceFile,
            Updated       = DateTime.UtcNow,
            Confidence    = validConfidence,
            Embedding     = embedding,
        };

        var indexed = await openSearch.IndexDocumentAsync(doc, cancellationToken).ConfigureAwait(false);

        var summary = content.Length > 120 ? content[..120].TrimEnd() + "…" : content;
        await files.UpdateIndexAsync(knowledgePath, title, domain, summary.Replace('\n', ' '), cancellationToken).ConfigureAwait(false);
        await files.AppendLogAsync("ingest", $"\"{title}\" → {knowledgePath}", cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            path = knowledgePath,
            indexed,
            semantic = embedding is not null,
        }, JsonOpts);
    }

    [McpServerTool(Name = "knowledge_approve")]
    [Description("Approve a pending review item from knowledge/_review/ and publish it to the knowledge library. Optionally override the suggested path.")]
    public async Task<string> KnowledgeApprove(
        [Description("Review ID, e.g. 'rev_abc12345' — as shown in knowledge_pending")] string reviewId,
        [Description("Override the suggested path. Leave blank to use the Librarian's suggestion.")] string? overridePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.KnowledgeLibrary.Enabled)
            return JsonSerializer.Serialize(new { error = "Knowledge library is not enabled." });

        if (string.IsNullOrWhiteSpace(reviewId))
            return JsonSerializer.Serialize(new { error = "reviewId is required" });

        var item = await files.GetReviewItemAsync(reviewId, cancellationToken).ConfigureAwait(false);
        if (item is null)
            return JsonSerializer.Serialize(new { error = $"Review item '{reviewId}' not found.", hint = "Use knowledge_pending to list pending items." });

        var targetPath = !string.IsNullOrWhiteSpace(overridePath) ? overridePath : item.SuggestedPath;
        if (string.IsNullOrWhiteSpace(targetPath))
            return JsonSerializer.Serialize(new { error = "No target path available. Provide overridePath." });

        var pathNorm = targetPath.StartsWith("knowledge/") ? targetPath["knowledge/".Length..] : targetPath;

        var titleMatch = System.Text.RegularExpressions.Regex.Match(item.DraftContent, @"^#\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
        var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(pathNorm);

        var bodyContent = titleMatch.Success
            ? item.DraftContent[(item.DraftContent.IndexOf('\n') + 1)..].TrimStart()
            : item.DraftContent;

        await files.WritePageAsync(pathNorm, title, bodyContent, item.SuggestedDomain, [], "medium", item.SourceFile, cancellationToken).ConfigureAwait(false);

        var embedding = await embeddings.GetEmbeddingAsync($"{title}\n\n{bodyContent}", cancellationToken).ConfigureAwait(false);
        var doc = new KnowledgeDocument
        {
            Title         = title,
            Content       = bodyContent,
            Domain        = item.SuggestedDomain,
            KnowledgePath = pathNorm,
            SourceFile    = item.SourceFile,
            Updated       = DateTime.UtcNow,
            Confidence    = "medium",
            Embedding     = embedding,
        };

        var indexed = await openSearch.IndexDocumentAsync(doc, cancellationToken).ConfigureAwait(false);

        var summary = bodyContent.Length > 120 ? bodyContent[..120].TrimEnd() + "…" : bodyContent;
        await files.UpdateIndexAsync(pathNorm, title, item.SuggestedDomain, summary.Replace('\n', ' '), cancellationToken).ConfigureAwait(false);
        await files.AppendLogAsync("approve", $"{reviewId} → {pathNorm}", cancellationToken).ConfigureAwait(false);

        files.DeleteReviewItem(reviewId);
        if (!string.IsNullOrWhiteSpace(item.SourceFile))
            files.ArchiveRawFile(Path.GetFileName(item.SourceFile));

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            reviewId,
            publishedTo = pathNorm,
            indexed,
            semantic = embedding is not null,
        }, JsonOpts);
    }

    [McpServerTool(Name = "knowledge_pending")]
    [Description("List files waiting in raw/pending/ and items in the _review/ queue awaiting human approval.")]
    public string KnowledgePending()
    {
        if (!options.KnowledgeLibrary.Enabled)
            return JsonSerializer.Serialize(new { error = "Knowledge library is not enabled." });

        var pending = files.ListPending();
        var review = files.ListReviewItems();

        return JsonSerializer.Serialize(new
        {
            rawPending = new
            {
                count = pending.Count,
                files = pending,
                hint = pending.Count > 0 ? "Ask the Librarian agent to run ingest on these files." : null,
            },
            reviewQueue = new
            {
                count = review.Count,
                items = review.Select(r => new
                {
                    r.ReviewId,
                    r.SuggestedPath,
                    r.SuggestedDomain,
                    r.Confidence,
                    r.Reason,
                    r.SubmittedAt,
                    approveWith = $"knowledge_approve(\"{r.ReviewId}\")",
                }),
            },
        }, JsonOpts);
    }

    [McpServerTool(Name = "knowledge_status")]
    [Description("Show knowledge library health: OpenSearch cluster status, indexed document count, and recent log entries.")]
    public async Task<string> KnowledgeStatus(CancellationToken cancellationToken = default)
    {
        var (osHealthy, osMessage) = await openSearch.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var docCount = await openSearch.GetDocumentCountAsync(cancellationToken).ConfigureAwait(false);
        var pending = options.KnowledgeLibrary.Enabled ? files.ListPending() : [];
        var reviewItems = options.KnowledgeLibrary.Enabled ? files.ListReviewItems() : [];

        string? recentLog = null;
        if (options.KnowledgeLibrary.Enabled)
        {
            try
            {
                var logPath = Path.Combine(options.KnowledgeLibrary.KnowledgePath, "_meta", "log.md");
                if (File.Exists(logPath))
                {
                    var lines = await File.ReadAllLinesAsync(logPath, cancellationToken).ConfigureAwait(false);
                    recentLog = string.Join('\n', lines.Where(l => l.StartsWith("## [")).TakeLast(5));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read log.md");
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = osHealthy ? "ok" : "degraded",
            opensearch = new
            {
                enabled = options.OpenSearch.Enabled,
                healthy = osHealthy,
                message = osMessage,
                indexedDocuments = docCount,
                index = options.OpenSearch.IndexName,
            },
            embeddings = new
            {
                enabled = options.Embeddings.Enabled,
                model = options.Embeddings.Model,
            },
            library = new
            {
                enabled = options.KnowledgeLibrary.Enabled,
                pendingRaw = pending.Count,
                pendingReview = reviewItems.Count,
                knowledgePath = options.KnowledgeLibrary.KnowledgePath,
            },
            recentLog,
        }, JsonOpts);
    }
}
