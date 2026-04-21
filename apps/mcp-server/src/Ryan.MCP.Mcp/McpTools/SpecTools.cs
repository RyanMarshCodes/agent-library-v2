using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using ModelContextProtocol.Server;
using Ryan.MCP.Mcp.Services;

namespace Ryan.MCP.Mcp.McpTools;

[McpServerToolType]
public sealed class SpecTools(DocumentIngestionCoordinator documents, ILogger<SpecTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [McpServerTool(Name = "parse_openapi")]
    [Description(
        "Parse an OpenAPI/Swagger spec from the documents store and return a structured summary: endpoints grouped by tag, " +
        "request/response schemas, auth schemes, and client architecture recommendations. " +
        "Use list_standards to find the spec path, then pass tier and path here.")]
    public async Task<string> ParseOpenApi(
        [Description("Document tier: 'official', 'organization', or 'project'")] string tier,
        [Description("Relative path to the spec file, e.g. 'my-api/openapi.json' or 'petstore.yaml'")] string path,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ToolName"] = "SpecTools.ParseOpenApi",
            ["Tier"] = tier,
            ["Path"] = path,
        });
        logger.LogDebug("ParseOpenApi invoked");

        var entry = documents.Snapshot.Documents.FirstOrDefault(d =>
            d.Tier.Equals(tier, StringComparison.OrdinalIgnoreCase) &&
            d.RelativePath.Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Spec '{path}' not found in tier '{tier}'.",
                hint = "Use list_standards to see available documents and their exact paths.",
            });
        }

        try
        {
            await using var stream = File.OpenRead(entry.AbsolutePath);
            var reader = new OpenApiStreamReader();
            var doc = reader.Read(stream, out var diagnostic);

            if (diagnostic.Errors.Count > 0)
            {
                logger.LogWarning("OpenAPI parse warnings for {Path}: {Errors}",
                    path, string.Join("; ", diagnostic.Errors.Select(e => e.Message)));
            }

            return JsonSerializer.Serialize(BuildSummary(doc, path, diagnostic), JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse OpenAPI spec: {Path}", path);
            return JsonSerializer.Serialize(new { error = $"Failed to parse spec: {ex.Message}" });
        }
    }

    private static object BuildSummary(OpenApiDocument doc, string path, OpenApiDiagnostic diagnostic)
    {
        // Raw schemes for architecture hints
        var rawSchemes = doc.Components?.SecuritySchemes?.Values.ToList()
            ?? (IReadOnlyCollection<OpenApiSecurityScheme>)[];

        // Serializable auth scheme summaries
        var authSchemes = doc.Components?.SecuritySchemes?
            .Select(kvp => new
            {
                name = kvp.Key,
                type = kvp.Value.Type.ToString(),
                scheme = kvp.Value.Scheme,
                bearerFormat = kvp.Value.BearerFormat,
                flows = kvp.Value.Flows != null ? SummarizeOAuthFlows(kvp.Value.Flows) : null,
                inLocation = kvp.Value.In.ToString(),
                parameterName = kvp.Value.Name,
                description = kvp.Value.Description,
            })
            .ToList() ?? [];

        // Endpoints grouped by tag
        var endpointsByTag = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pathStr, pathItem) in doc.Paths ?? new OpenApiPaths())
        {
            foreach (var (method, operation) in pathItem.Operations)
            {
                var tags = operation.Tags?.Select(t => t.Name).ToList() is { Count: > 0 } t ? t : ["untagged"];
                var summary = new
                {
                    method = method.ToString().ToUpperInvariant(),
                    path = pathStr,
                    operationId = operation.OperationId,
                    summary = operation.Summary,
                    deprecated = operation.Deprecated,
                    parameters = operation.Parameters?
                        .Select(p => new { p.Name, location = p.In.ToString(), p.Required })
                        .ToList(),
                    requestBody = operation.RequestBody != null
                        ? operation.RequestBody.Content.Keys.FirstOrDefault()
                        : null,
                    responses = operation.Responses?
                        .Select(r => new { status = r.Key, description = r.Value.Description })
                        .ToList(),
                    security = operation.Security?.Select(s => string.Join(", ", s.Keys.Select(k => k.Reference.Id))).ToList(),
                };

                foreach (var tag in tags)
                {
                    if (!endpointsByTag.TryGetValue(tag, out var list))
                        endpointsByTag[tag] = list = [];
                    list.Add(summary);
                }
            }
        }

        // Top-level schemas (models)
        var schemas = doc.Components?.Schemas?
            .Select(kvp => new
            {
                name = kvp.Key,
                type = kvp.Value.Type,
                properties = kvp.Value.Properties?.Select(p => new
                {
                    name = p.Key,
                    type = p.Value.Type ?? p.Value.Reference?.Id,
                    nullable = p.Value.Nullable,
                    required = kvp.Value.Required?.Contains(p.Key) ?? false,
                }).ToList(),
                required = kvp.Value.Required?.ToList(),
            })
            .ToList() ?? [];

        var totalEndpoints = endpointsByTag.Values.Sum(v => v.Count);
        var deprecatedCount = doc.Paths?
            .SelectMany(p => p.Value.Operations.Values)
            .Count(o => o.Deprecated) ?? 0;

        return new
        {
            specPath = path,
            title = doc.Info?.Title,
            version = doc.Info?.Version,
            description = doc.Info?.Description,
            servers = doc.Servers?.Select(s => s.Url).ToList(),
            stats = new
            {
                totalEndpoints,
                deprecatedEndpoints = deprecatedCount,
                tagCount = endpointsByTag.Count,
                schemaCount = schemas.Count,
                authSchemeCount = authSchemes.Count,
            },
            authSchemes,
            endpointsByTag,
            schemas,
            parseWarnings = diagnostic.Errors.Select(e => e.Message).ToList(),
            clientArchitectureHints = BuildArchitectureHints(totalEndpoints, rawSchemes, endpointsByTag, schemas.Count),
        };
    }

    private static List<string> BuildArchitectureHints(
        int totalEndpoints,
        IReadOnlyCollection<OpenApiSecurityScheme> authSchemes,
        Dictionary<string, List<object>> endpointsByTag,
        int schemaCount)
    {
        var hints = new List<string>();

        if (totalEndpoints > 20)
            hints.Add($"Large API ({totalEndpoints} endpoints across {endpointsByTag.Count} tag groups) — consider code generation (Kiota or NSwag) rather than a hand-written client.");
        else
            hints.Add($"Small API ({totalEndpoints} endpoints) — a hand-written, typed client is manageable and gives more control over the abstraction.");

        if (authSchemes.Any(s => s.Type == SecuritySchemeType.OAuth2))
            hints.Add("OAuth2 detected — implement a token cache with proactive refresh. Use MSAL (C#) or a dedicated OAuth2 library. Never store tokens in localStorage.");

        if (authSchemes.Any(s => s.Type == SecuritySchemeType.ApiKey))
            hints.Add("API key auth detected — inject via a DelegatingHandler (C#) or request interceptor. Never log URLs that contain the key as a query parameter.");

        if (authSchemes.Any(s => s.Type == SecuritySchemeType.Http && s.Scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase)))
            hints.Add("Bearer token auth detected — inject via Authorization header in a DelegatingHandler. Cache tokens and refresh before expiry.");

        if (schemaCount > 15)
            hints.Add($"{schemaCount} schemas detected — keep generated DTOs in their own namespace/module. Map to domain types at the client facade layer, not in application code.");

        if (endpointsByTag.Keys.Any(t => t.Equals("untagged", StringComparison.OrdinalIgnoreCase)))
            hints.Add("Some endpoints are untagged — group them manually in your client design to avoid a catch-all class.");

        hints.Add("Wrap all client calls in a facade layer that maps HTTP errors to typed exceptions. Never let raw HTTP status codes propagate into domain code.");
        hints.Add("Implement retry with exponential backoff for 429, 502, 503, 504. Never retry 4xx errors (except 429).");

        return hints;
    }

    private static object SummarizeOAuthFlows(OpenApiOAuthFlows flows)
    {
        var result = new List<string>();
        if (flows.ClientCredentials != null) result.Add("clientCredentials");
        if (flows.AuthorizationCode != null) result.Add("authorizationCode");
        if (flows.Implicit != null) result.Add("implicit (deprecated)");
        if (flows.Password != null) result.Add("password (deprecated)");
        return result;
    }
}
