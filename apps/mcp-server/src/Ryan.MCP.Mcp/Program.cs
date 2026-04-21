using System.Reflection;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Ryan.MCP.Mcp.Configuration;
using Ryan.MCP.Mcp.Services;
using Ryan.MCP.Mcp.Services.Memory;
using Ryan.MCP.Mcp.Services.ModelMapping;
using Ryan.MCP.Mcp.Storage;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

var serviceName = builder.Configuration["McpOptions:Knowledge:ProjectSlug"] ?? "Ryan.MCP";
var serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", serviceName)
    .Enrich.WithProperty("ServiceVersion", serviceVersion)
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console()
    .WriteTo.OpenTelemetry(options =>
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? builder.Configuration["Otlp:Endpoint"]
            ?? "http://localhost:4317";
        options.Endpoint = otlpEndpoint;
        options.Protocol = OtlpProtocol.Grpc;
        options.ResourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = serviceName,
            ["service.version"] = serviceVersion,
            ["deployment.environment"] = builder.Environment.EnvironmentName,
        };
    })
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("Ryan.MCP"));

// MCP Options
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));
var mcpOptions = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
builder.Services.AddSingleton(mcpOptions);

// Use PascalCase JSON for all Results.Ok() responses — the management UI accesses properties by PascalCase name
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = null);

// HTTP client for fetch tool
builder.Services.AddHttpClient("fetch", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    var slug = builder.Configuration["McpOptions:Knowledge:ProjectSlug"] ?? "mcp-server";
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"{slug}/1.0 (MCP fetch tool)");
});

// Core services
builder.Services.AddSingleton<ExternalConnectorRegistry>();
builder.Services.AddSingleton<ExternalMcpClientService>();
builder.Services.AddSingleton<DocumentIngestionCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DocumentIngestionCoordinator>());
builder.Services.AddSingleton<AgentIngestionCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentIngestionCoordinator>());
builder.Services.AddSingleton<IStorageService>(sp =>
{
    var options = sp.GetRequiredService<McpOptions>();
    var logger = sp.GetRequiredService<ILogger<FileStorageService>>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var logger2 = sp.GetRequiredService<ILogger<AzureBlobStorageService>>();
    
    return options.Storage.Provider.ToLowerInvariant() switch
    {
        "azure" => new AzureBlobStorageService(options, logger2),
        _ => new FileStorageService(options, env, logger)
    };
});
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<McpOptions>>().Value;
    var cs = opts.MemoryStore.ConnectionString;
    var builder = new NpgsqlDataSourceBuilder(cs);
    return builder.Build();
});
builder.Services.AddSingleton<IMemoryStore, PostgresMemoryStore>();
builder.Services.AddSingleton<MemoryMigrationRunner>();
builder.Services.AddSingleton<IModelMappingStore, PostgresModelMappingStore>();
builder.Services.AddSingleton<ModelMappingSyncService>();

// MCP Protocol (tools, resources, prompts discovered via attributes)
// Stateless = true needed for remote MCP clients (OpenCode) that don't maintain session state
#pragma warning disable MCP9004
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
    })
    .WithToolsFromAssembly(Assembly.GetExecutingAssembly())
    .WithPromptsFromAssembly(Assembly.GetExecutingAssembly())
    .WithResourcesFromAssembly(Assembly.GetExecutingAssembly());
#pragma warning restore MCP9004

var app = builder.Build();

// Ensure memory schema exists before handling requests.
using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<MemoryMigrationRunner>();
    await runner.RunAsync().ConfigureAwait(false);
}

// Sync model mappings from agent frontmatter after ingestion completes.
// Runs in background — non-blocking.
_ = Task.Run(async () =>
{
    // Wait for agent ingestion to finish (it runs as a hosted service)
    var agentCoordinator = app.Services.GetRequiredService<AgentIngestionCoordinator>();
    var maxWait = TimeSpan.FromSeconds(30);
    var waited = TimeSpan.Zero;
    while (agentCoordinator.Snapshot.TotalAgents == 0 && waited < maxWait)
    {
        await Task.Delay(500).ConfigureAwait(false);
        waited += TimeSpan.FromMilliseconds(500);
    }

    try
    {
        var syncService = app.Services.GetRequiredService<ModelMappingSyncService>();
        var result = await syncService.SyncAsync().ConfigureAwait(false);
        app.Logger.LogInformation(
            "Model mapping sync: {Synced} synced, {Partial} partial, {Skipped} skipped",
            result.Synced, result.PartiallyParsed, result.Skipped);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Model mapping sync failed on startup (non-fatal)");
    }
});

// Error handling for MCP endpoint - catches JSON parse errors before they crash the server
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (System.Text.Json.JsonException ex) when (context.Request.Path.StartsWithSegments("/mcp"))
    {
        app.Logger.LogWarning(ex, "MCP endpoint received malformed JSON request");
        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"jsonrpc":"2.0","error":{"code":-32700,"message":"Parse error"}}""");
    }
});

app.UseStaticFiles();

var connectorRegistry = app.Services.GetRequiredService<ExternalConnectorRegistry>();
app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation(
        "Ryan.MCP startup: {ConfiguredCount} connector(s) configured, {EnabledCount} enabled.",
        connectorRegistry.Configured.Count,
        connectorRegistry.Enabled.Count);
});

// ─── MCP Protocol endpoint (for IDEs / Claude Code / Cursor / OpenCode) ──────
app.MapMcp("/mcp");

// ─── MCP client connection logging ─────────────────────────────────────────
app.Use(async (context, next) =>
{
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var userAgent = context.Request.Headers.UserAgent.ToString();
    var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "none";

    if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.ContentType?.Contains("json") == true)
    {
        try
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            context.Request.Body.Position = 0;

            if (!string.IsNullOrEmpty(body))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("method", out var method) && method.GetString() == "initialize")
                {
                    var clientInfo = root.TryGetProperty("params", out var p) && p.TryGetProperty("clientInfo", out var ci) ? ci : default;
                    var clientName = clientInfo.ValueKind != System.Text.Json.JsonValueKind.Undefined && clientInfo.TryGetProperty("name", out var cn) ? cn.GetString() : null;
                    var clientVersion = clientInfo.ValueKind != System.Text.Json.JsonValueKind.Undefined && clientInfo.TryGetProperty("version", out var cv) ? cv.GetString() : null;

                    app.Logger.LogInformation(
                        "MCP client connected: ClientIP={ClientIp}, ClientName={ClientName}, ClientVersion={ClientVersion}, UserAgent={UserAgent}, TraceId={TraceId}",
                        clientIp,
                        string.IsNullOrEmpty(clientName) ? "unknown" : clientName,
                        string.IsNullOrEmpty(clientVersion) ? "unknown" : clientVersion,
                        string.IsNullOrEmpty(userAgent) ? "none" : userAgent,
                        traceId);
                }
                else
                {
                    app.Logger.LogInformation(
                        "MCP request: ClientIP={ClientIp}, UserAgent={UserAgent}, TraceId={TraceId}",
                        clientIp,
                        string.IsNullOrEmpty(userAgent) ? "none" : userAgent,
                        traceId);
                }
            }
            else
            {
                app.Logger.LogInformation(
                    "MCP request: ClientIP={ClientIp}, UserAgent={UserAgent}, TraceId={TraceId}",
                    clientIp,
                    string.IsNullOrEmpty(userAgent) ? "none" : userAgent,
                    traceId);
            }
        }
        catch
        {
            app.Logger.LogInformation(
                "MCP request: ClientIP={ClientIp}, UserAgent={UserAgent}, TraceId={TraceId}",
                clientIp,
                string.IsNullOrEmpty(userAgent) ? "none" : userAgent,
                traceId);
        }
    }
    else
    {
        await next().ConfigureAwait(false);
        return;
    }

    await next().ConfigureAwait(false);
});

// ─── Health ───────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Ryan.MCP.Mcp",
    utc = DateTime.UtcNow,
}));

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "alive",
    service = "Ryan.MCP.Mcp",
    utc = DateTime.UtcNow,
}));

app.MapGet("/health/ready", async (
    AgentIngestionCoordinator agents,
    DocumentIngestionCoordinator docs,
    IMemoryStore memoryStore,
    CancellationToken ct) =>
{
    var (memoryAvailable, memoryMessage) = await memoryStore.CheckAvailabilityAsync(ct).ConfigureAwait(false);
    var agentReady = agents.Snapshot.TotalAgents > 0;
    var docReady = docs.Snapshot.TotalDocuments >= 0;
    var ready = memoryAvailable && agentReady && docReady;

    return Results.Ok(new
    {
        status = ready ? "ready" : "not_ready",
        checks = new
        {
            memory = new { ok = memoryAvailable, message = memoryMessage },
            agents = new { ok = agentReady, count = agents.Snapshot.TotalAgents },
            documents = new { ok = docReady, count = docs.Snapshot.TotalDocuments },
        },
        utc = DateTime.UtcNow,
    });
});

// ─── REST API for management UI ───────────────────────────────────────────────

// Agents
app.MapGet("/api/agents", (AgentIngestionCoordinator agents, string? scope = null, string? tags = null) =>
{
    var tagList = string.IsNullOrWhiteSpace(tags)
        ? []
        : tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();

    var results = agents.ListAgents(scope, tagList);
    return Results.Ok(new
    {
        count = results.Count,
        scope,
        agents = results.Select(a => new
        {
            a.Name,
            a.Description,
            a.Scope,
            a.Tags,
            a.Format,
            a.FileName,
            a.SizeBytes,
            a.LastWriteUtc,
            a.IndexedUtc,
        }),
    });
});

app.MapGet("/api/agents/{name}", (string name, AgentIngestionCoordinator agents) =>
{
    var agent = agents.GetAgent(name);
    return agent == null
        ? Results.NotFound(new { error = $"Agent '{name}' not found" })
        : Results.Ok(agent);
});

app.MapGet("/api/agents/search", (string query, AgentIngestionCoordinator agents) =>
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.BadRequest(new { error = "query is required" });
    }

    var results = agents.SearchAgents(query);
    return Results.Ok(new { query, count = results.Count, agents = results });
});

app.MapGet("/api/agents/status", (AgentIngestionCoordinator agents) =>
{
    var s = agents.Snapshot;
    return Results.Ok(new
    {
        s.ProjectSlug,
        s.LastStartedUtc,
        s.UpdatedUtc,
        s.TotalAgents,
        s.ByScope,
        s.ByFormat,
        s.ScanRoots,
    });
});

app.MapPost("/api/agents/ingest", async (AgentIngestionCoordinator agents, CancellationToken ct) =>
{
    await agents.TriggerReindexAsync(ct).ConfigureAwait(false);
    var s = agents.Snapshot;
    return Results.Ok(new { status = "ok", s.UpdatedUtc, s.TotalAgents, s.ByScope });
});

app.MapPost("/api/agents/upload", async (HttpRequest request, IStorageService storage, AgentIngestionCoordinator agents, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Multipart form required" });
    }

    var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
    var uploaded = new List<object>();
    var failed = new List<object>();

    foreach (var file in form.Files)
    {
        await using var stream = file.OpenReadStream();
        var (success, message, filePath) = await storage.SaveAgentAsync(file.FileName, stream, ct).ConfigureAwait(false);
        if (success)
        {
            uploaded.Add(new { file.FileName, message });
        }
        else
        {
            failed.Add(new { file.FileName, message });
        }
    }

    if (uploaded.Count > 0)
    {
        await agents.TriggerReindexAsync(ct).ConfigureAwait(false);
    }

    return Results.Ok(new { uploaded, failed });
});

app.MapDelete("/api/agents/{fileName}", async (string fileName, IStorageService storage, AgentIngestionCoordinator agents, CancellationToken ct) =>
{
    var (success, message) = storage.DeleteAgent(fileName);
    if (!success)
    {
        return Results.NotFound(new { error = message });
    }

    await agents.TriggerReindexAsync(ct).ConfigureAwait(false);
    return Results.Ok(new { message });
});

// Documents
app.MapGet("/api/documents", (DocumentIngestionCoordinator docs) =>
{
    var s = docs.Snapshot;
    return Results.Ok(new
    {
        s.ProjectSlug,
        s.TotalDocuments,
        s.ByTier,
        s.Roots,
        s.LastStartedUtc,
        s.UpdatedUtc,
        documents = s.Documents.Select(d => new
        {
            d.Tier,
            d.RelativePath,
            d.SizeBytes,
            d.LastWriteUtc,
        }),
    });
});

app.MapPost("/api/documents/ingest", async (DocumentIngestionCoordinator docs, CancellationToken ct) =>
{
    await docs.TriggerReindexAsync(ct).ConfigureAwait(false);
    var s = docs.Snapshot;
    return Results.Ok(new { status = "ok", s.UpdatedUtc, s.TotalDocuments, s.ByTier });
});

app.MapPost("/api/documents/{tier}/upload", async (string tier, HttpRequest request, IStorageService storage, DocumentIngestionCoordinator docs, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Multipart form required" });
    }

    var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
    var uploaded = new List<object>();
    var failed = new List<object>();

    foreach (var file in form.Files)
    {
        await using var stream = file.OpenReadStream();
        var (success, message, filePath) = await storage.SaveDocumentAsync(tier, file.FileName, stream, ct).ConfigureAwait(false);
        if (success)
        {
            uploaded.Add(new { file.FileName, message });
        }
        else
        {
            failed.Add(new { file.FileName, message });
        }
    }

    if (uploaded.Count > 0)
    {
        await docs.TriggerReindexAsync(ct).ConfigureAwait(false);
    }

    return Results.Ok(new { tier, uploaded, failed });
});

app.MapDelete("/api/documents/{tier}/{*relativePath}", async (string tier, string relativePath, IStorageService storage, DocumentIngestionCoordinator docs, CancellationToken ct) =>
{
    var (success, message) = storage.DeleteDocument(tier, relativePath);
    if (!success)
    {
        return Results.NotFound(new { error = message });
    }

    await docs.TriggerReindexAsync(ct).ConfigureAwait(false);
    return Results.Ok(new { message });
});

// External connectors
app.MapGet("/api/connectors", (ExternalConnectorRegistry connectors) =>
    Results.Ok(new
    {
        configuredCount = connectors.Configured.Count,
        enabledCount = connectors.Enabled.Count,
        connectors = connectors.Configured.Select(c => new
        {
            c.Name,
            c.Enabled,
            c.Transport,
            c.Endpoint,
            c.TimeoutMs,
        }),
    }));

// Serve SPA for all other routes (management UI)
app.MapFallbackToFile("index.html");

app.Run();
