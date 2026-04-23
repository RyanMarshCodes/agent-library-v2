using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.Knowledge;

public sealed class KnowledgeDocument
{
    [JsonPropertyName("title")]        public string Title { get; set; } = "";
    [JsonPropertyName("content")]      public string Content { get; set; } = "";
    [JsonPropertyName("domain")]       public string Domain { get; set; } = "";
    [JsonPropertyName("tags")]         public string[] Tags { get; set; } = [];
    [JsonPropertyName("knowledge_path")] public string KnowledgePath { get; set; } = "";
    [JsonPropertyName("source_file")]  public string? SourceFile { get; set; }
    [JsonPropertyName("updated")]      public DateTime Updated { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("confidence")]   public string Confidence { get; set; } = "medium";
    [JsonPropertyName("embedding")]    public float[]? Embedding { get; set; }
}

public sealed class KnowledgeSearchResult
{
    public string KnowledgePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Domain { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public string Snippet { get; set; } = "";
    public double Score { get; set; }
    public string Confidence { get; set; } = "";
    public DateTime Updated { get; set; }
}

public sealed class OpenSearchService(
    IHttpClientFactory httpClientFactory,
    McpOptions options,
    ILogger<OpenSearchService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string BaseUrl => options.OpenSearch.Url.TrimEnd('/');
    private string Index => options.OpenSearch.IndexName;
    private string Pipeline => options.OpenSearch.PipelineName;

    public async Task EnsureInfrastructureAsync(CancellationToken ct)
    {
        if (!options.OpenSearch.Enabled) return;

        using var http = httpClientFactory.CreateClient("opensearch");

        await EnsureIndexAsync(http, ct).ConfigureAwait(false);
        await EnsurePipelineAsync(http, ct).ConfigureAwait(false);
    }

    private async Task EnsureIndexAsync(HttpClient http, CancellationToken ct)
    {
        var response = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, $"{BaseUrl}/{Index}"), ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation("Creating OpenSearch index {Index}", Index);
            var mapping = new
            {
                settings = new { index = new { knn = true } },
                mappings = new
                {
                    properties = new Dictionary<string, object>
                    {
                        ["title"]          = new { type = "text", boost = 3 },
                        ["content"]        = new { type = "text" },
                        ["domain"]         = new { type = "keyword" },
                        ["tags"]           = new { type = "keyword" },
                        ["knowledge_path"] = new { type = "keyword" },
                        ["source_file"]    = new { type = "keyword" },
                        ["updated"]        = new { type = "date" },
                        ["confidence"]     = new { type = "keyword" },
                        ["embedding"]      = new
                        {
                            type = "knn_vector",
                            dimension = options.Embeddings.Dimensions,
                            method = new
                            {
                                name = "hnsw",
                                space_type = "cosinesimil",
                                engine = "nmslib",
                            },
                        },
                    },
                },
            };

            var json = JsonSerializer.Serialize(mapping, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var create = await http.PutAsync($"{BaseUrl}/{Index}", content, ct).ConfigureAwait(false);
            if (!create.IsSuccessStatusCode)
            {
                var body = await create.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                logger.LogWarning("Failed to create index {Index}: {Body}", Index, body);
            }
        }
    }

    private async Task EnsurePipelineAsync(HttpClient http, CancellationToken ct)
    {
        var response = await http.GetAsync(
            $"{BaseUrl}/_search/pipeline/{Pipeline}", ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Creating search pipeline {Pipeline}", Pipeline);
            var pipeline = new
            {
                description = "Hybrid BM25 + kNN pipeline for knowledge-wiki",
                phase_results_processors = new[]
                {
                    new
                    {
                        normalization_processor = new
                        {
                            normalization = new { technique = "min_max" },
                            combination = new
                            {
                                technique = "arithmetic_mean",
                                parameters = new { weights = new[] { 0.4f, 0.6f } },
                            },
                        },
                    },
                },
            };

            var json = JsonSerializer.Serialize(pipeline, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var create = await http.PutAsync(
                $"{BaseUrl}/_search/pipeline/{Pipeline}", content, ct).ConfigureAwait(false);

            if (!create.IsSuccessStatusCode)
            {
                var body = await create.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                logger.LogWarning("Failed to create pipeline {Pipeline}: {Body}", Pipeline, body);
            }
        }
    }

    public async Task<bool> IndexDocumentAsync(KnowledgeDocument document, CancellationToken ct)
    {
        if (!options.OpenSearch.Enabled) return false;

        using var http = httpClientFactory.CreateClient("opensearch");
        var docId = Uri.EscapeDataString(document.KnowledgePath);
        var json = JsonSerializer.Serialize(document, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.PutAsync(
            $"{BaseUrl}/{Index}/_doc/{docId}", content, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogWarning("Failed to index document {Path}: {Body}", document.KnowledgePath, body);
            return false;
        }

        logger.LogDebug("Indexed document {Path}", document.KnowledgePath);
        return true;
    }

    public async Task<List<KnowledgeSearchResult>> SearchAsync(
        string query,
        float[]? embedding,
        int maxResults,
        string? domain,
        string[]? tags,
        CancellationToken ct)
    {
        if (!options.OpenSearch.Enabled) return [];

        using var http = httpClientFactory.CreateClient("opensearch");

        var queries = new List<object>
        {
            new
            {
                multi_match = new
                {
                    query,
                    fields = new[] { "title^3", "content" },
                    type = "best_fields",
                },
            },
        };

        if (embedding is { Length: > 0 })
        {
            queries.Add(new
            {
                knn = new
                {
                    embedding = new
                    {
                        vector = embedding,
                        k = options.OpenSearch.KnnCandidates,
                    },
                },
            });
        }

        object queryBody;
        if (embedding is { Length: > 0 })
        {
            queryBody = new { hybrid = new { queries } };
        }
        else
        {
            queryBody = queries[0];
        }

        var filterClauses = new List<object>();
        if (!string.IsNullOrWhiteSpace(domain))
            filterClauses.Add(new { term = new Dictionary<string, string> { ["domain"] = domain } });
        if (tags is { Length: > 0 })
            filterClauses.Add(new { terms = new Dictionary<string, string[]> { ["tags"] = tags } });

        object searchBody;
        if (filterClauses.Count > 0)
        {
            searchBody = new
            {
                size = maxResults,
                query = new
                {
                    @bool = new
                    {
                        must = queryBody,
                        filter = filterClauses,
                    },
                },
                _source = new[] { "title", "domain", "tags", "knowledge_path", "content", "confidence", "updated" },
            };
        }
        else
        {
            searchBody = new
            {
                size = maxResults,
                query = queryBody,
                _source = new[] { "title", "domain", "tags", "knowledge_path", "content", "confidence", "updated" },
            };
        }

        var json = JsonSerializer.Serialize(searchBody, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var url = embedding is { Length: > 0 }
            ? $"{BaseUrl}/{Index}/_search?search_pipeline={Pipeline}"
            : $"{BaseUrl}/{Index}/_search";

        var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            logger.LogWarning("Search failed: {Body}", body);
            return [];
        }

        using var responseDoc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var hits = responseDoc.RootElement
            .GetProperty("hits")
            .GetProperty("hits");

        var results = new List<KnowledgeSearchResult>();
        foreach (var hit in hits.EnumerateArray())
        {
            var src = hit.GetProperty("_source");
            var contentText = src.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var snippet = contentText.Length > 200
                ? contentText[..200].TrimEnd() + "…"
                : contentText;

            results.Add(new KnowledgeSearchResult
            {
                KnowledgePath = src.TryGetProperty("knowledge_path", out var kp) ? kp.GetString() ?? "" : "",
                Title         = src.TryGetProperty("title",          out var t)  ? t.GetString()  ?? "" : "",
                Domain        = src.TryGetProperty("domain",         out var d)  ? d.GetString()  ?? "" : "",
                Tags          = src.TryGetProperty("tags",           out var tg) ? tg.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
                Confidence    = src.TryGetProperty("confidence",     out var co) ? co.GetString() ?? "" : "",
                Updated       = src.TryGetProperty("updated",        out var u)  && u.TryGetDateTime(out var dt) ? dt : default,
                Snippet       = snippet,
                Score         = hit.TryGetProperty("_score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0,
            });
        }

        return results;
    }

    public async Task<bool> DeleteDocumentAsync(string knowledgePath, CancellationToken ct)
    {
        if (!options.OpenSearch.Enabled) return false;

        using var http = httpClientFactory.CreateClient("opensearch");
        var docId = Uri.EscapeDataString(knowledgePath);
        var response = await http.DeleteAsync($"{BaseUrl}/{Index}/_doc/{docId}", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<(bool healthy, string message)> CheckHealthAsync(CancellationToken ct)
    {
        if (!options.OpenSearch.Enabled) return (false, "OpenSearch disabled");

        try
        {
            using var http = httpClientFactory.CreateClient("opensearch");
            var response = await http.GetAsync($"{BaseUrl}/_cluster/health", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
            return (status is "green" or "yellow", $"cluster status: {status}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<long> GetDocumentCountAsync(CancellationToken ct)
    {
        if (!options.OpenSearch.Enabled) return 0;

        try
        {
            using var http = httpClientFactory.CreateClient("opensearch");
            var response = await http.GetAsync($"{BaseUrl}/{Index}/_count", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("count", out var c) ? c.GetInt64() : 0;
        }
        catch
        {
            return 0;
        }
    }
}
