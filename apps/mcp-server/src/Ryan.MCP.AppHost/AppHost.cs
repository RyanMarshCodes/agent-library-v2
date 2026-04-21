var builder = DistributedApplication.CreateBuilder(args);

// PROJECT_SLUG drives all naming: project slug, DB name, and container prefixes.
// Override via env var to run multiple isolated instances on the same machine.
// e.g. set PROJECT_SLUG=work-mcp on your work laptop.
var projectSlug = Environment.GetEnvironmentVariable("PROJECT_SLUG") ?? "ryan-mcp";
var dbName = projectSlug.Replace("-", "_"); // ryan-mcp → ryan_mcp, work-mcp → work_mcp

static bool IsEnabled(IConfiguration config, string key, bool defaultValue = false)
{
    var value = config[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

static int GetPort(IConfiguration config, string key, int defaultValue)
{
    var value = config[key];
    return int.TryParse(value, out var parsed) && parsed is > 0 and <= 65535
        ? parsed
        : defaultValue;
}

// Resolve paths relative to repo root (3 levels up from AppHost dir)
var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".."));
var agentsPath = Path.Combine(repoRoot, "agents");
var globalDocsPath = Path.Combine(repoRoot, "knowledge", "global");
var backendDocsPath = Path.Combine(repoRoot, "knowledge", "backend");
var frontendDocsPath = Path.Combine(repoRoot, "knowledge", "frontend");
var localAgentsPath = Path.Combine(repoRoot, ".local", "agents");
var indexPath = Path.Combine(repoRoot, ".local", "mcp-index");
var memoryDataPath = Path.Combine(repoRoot, ".local", "memory");
var memoryPostgresDataPath = Path.Combine(repoRoot, ".local", "postgres-memory");

var memoryPostgresPort = GetPort(builder.Configuration, "Projects:Ports:MemoryPostgres", 8810);
var sequentialThinkingPort = GetPort(builder.Configuration, "Projects:Ports:SequentialThinking", 8788);
var azureMcpPort = GetPort(builder.Configuration, "Projects:Ports:Azure", 8790);
var dockerMcpPort = GetPort(builder.Configuration, "Projects:Ports:Docker", 8791);
var discordMcpPort = GetPort(builder.Configuration, "Projects:Ports:Discord", 8792);
var filesystemMcpPort = GetPort(builder.Configuration, "Projects:Ports:Filesystem", 8793);
var fetchMcpPort = GetPort(builder.Configuration, "Projects:Ports:Fetch", 8800);
var legacyMemoryMcpPort = GetPort(builder.Configuration, "Projects:Ports:Memory", 8801);
var ollamaPort = GetPort(builder.Configuration, "Projects:Ports:Ollama", 11434);
var qdrantPort = GetPort(builder.Configuration, "Projects:Ports:Qdrant", 6333);

Directory.CreateDirectory(memoryDataPath);
Directory.CreateDirectory(memoryPostgresDataPath);

var mcpServer = builder.AddProject<Projects.Ryan_MCP_Mcp>("mcp-server")
    .WithEnvironment("McpOptions__Knowledge__ProjectSlug", projectSlug)
    .WithEnvironment("McpOptions__Knowledge__OfficialPath", globalDocsPath)
    .WithEnvironment("McpOptions__Knowledge__OrganizationPath", backendDocsPath)
    .WithEnvironment("McpOptions__Knowledge__ProjectPath", frontendDocsPath)
    .WithEnvironment("McpOptions__Knowledge__IndexPath", indexPath)
    .WithEnvironment("McpOptions__Agents__LocalPath", localAgentsPath)
    .WithEnvironment("McpOptions__Agents__LibraryPath", agentsPath)
    .WithEnvironment("McpOptions__Ingestion__WatchForChanges", "true")
    .WithEnvironment("McpOptions__Ingestion__AutoIngestOnStartup", "true");

var memoryPostgresEnabled = IsEnabled(builder.Configuration, "Projects:MemoryPostgres:Enabled", defaultValue: true);
if (memoryPostgresEnabled)
{
    var memoryPostgres = builder.AddContainer("memory-postgres", "postgres:16-alpine")
        .WithEnvironment("POSTGRES_DB", dbName)
        .WithEnvironment("POSTGRES_USER", "postgres")
        .WithEnvironment("POSTGRES_PASSWORD", "postgres")
        .WithEndpoint(name: "postgres", port: memoryPostgresPort, targetPort: 5432)
        .WithBindMount(source: memoryPostgresDataPath, target: "/var/lib/postgresql/data", isReadOnly: false)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-memory-postgres");

    mcpServer.WithEnvironment("McpOptions__MemoryStore__Provider", "postgres")
        .WithEnvironment("McpOptions__MemoryStore__ConnectionString", $"Host=localhost;Port={memoryPostgresPort};Database={dbName};Username=postgres;Password=postgres;Timeout=15;Command Timeout=30")
        .WithEnvironment("McpOptions__MemoryStore__CommandTimeoutSeconds", "30");
}

// ── Remote HTTP connectors (no containers — zero machine overhead) ────────────
//
// GitHub MCP: https://api.githubcopilot.com/mcp/
//   Set GITHUB_TOKEN env var and enable "github" in McpOptions.ExternalConnectors.
//   Requires GitHub Copilot subscription (individual, business, or enterprise).
//
// Azure DevOps MCP: https://github.com/microsoft/azure-devops-mcp
//   Run once: docker run -e AZURE_DEVOPS_PAT=... <image> --transport http --port 8794
//   Or host it elsewhere and point Endpoint at your URL.
//   Set AZURE_DEVOPS_PAT env var and enable "azure-devops" in ExternalConnectors.

// ── Docker containers (all disabled by default — opt in as needed) ────────────

// Sequential Thinking — chain-of-thought reasoning (enabled by default, very small)
var sequentialThinkingEnabled = IsEnabled(builder.Configuration, "Projects:SequentialThinking:Enabled", defaultValue: true);
if (sequentialThinkingEnabled)
{
    var sequentialThinking = builder.AddContainer("sequentialthinking", "mcp/sequentialthinking:latest")
        .WithEndpoint(name: "mcp", port: sequentialThinkingPort, targetPort: 8788)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-sequentialthinking");

    mcpServer.WithEnvironment("McpOptions__ExternalConnectorEndpointOverrides__sequential-thinking",
        sequentialThinking.GetEndpoint("mcp"));
}

// Azure MCP — Azure resource management via Azure CLI credentials.
// Run `az login` on the host before starting. Covers subscriptions, resources,
// Key Vault, Storage, App Service, AKS, and more.
var azureEnabled = IsEnabled(builder.Configuration, "Projects:Azure:Enabled");
if (azureEnabled)
{
    var azureMcp = builder.AddContainer("azure-mcp", "mcr.microsoft.com/azure-mcp-server:latest")
        .WithEndpoint(name: "mcp", port: azureMcpPort, targetPort: 8790)
        .WithEnvironment("AZURE_AUTH_MODE", "cli")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-azure");

    mcpServer.WithEnvironment("McpOptions__ExternalConnectorEndpointOverrides__azure",
        azureMcp.GetEndpoint("mcp"));
}

// Docker MCP — manage containers, images, volumes from within AI context.
var dockerEnabled = IsEnabled(builder.Configuration, "Projects:Docker:Enabled");
if (dockerEnabled)
{
    var dockerMcp = builder.AddContainer("docker-mcp", "mcp/server-docker:latest")
        .WithEndpoint(name: "mcp", port: dockerMcpPort, targetPort: 8791)
        .WithBindMount(source: "/var/run/docker.sock", target: "/var/run/docker.sock", isReadOnly: true)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-docker");

    mcpServer.WithEnvironment("McpOptions__ExternalConnectorEndpointOverrides__docker",
        dockerMcp.GetEndpoint("mcp"));
}

// Discord MCP — send notifications to Discord channels.
// Set DISCORD_BOT_TOKEN env var.
var discordEnabled = IsEnabled(builder.Configuration, "Projects:Discord:Enabled");
if (discordEnabled)
{
    var discordMcp = builder.AddContainer("discord-mcp", "ghcr.io/saseq/discord-mcp:latest")
        .WithEndpoint(name: "mcp", port: discordMcpPort, targetPort: 8792)
        .WithEnvironment("DISCORD_BOT_TOKEN", Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? "")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-discord");

    mcpServer.WithEnvironment("McpOptions__ExternalConnectorEndpointOverrides__discord",
        discordMcp.GetEndpoint("mcp"));
}

// Filesystem MCP — controlled read/write access to files outside the project.
// Mounts the repo root as /data (read-only).
var filesystemEnabled = IsEnabled(builder.Configuration, "Projects:Filesystem:Enabled");
if (filesystemEnabled)
{
    var filesystemMcp = builder.AddContainer("filesystem-mcp", "mcp/server-filesystem:latest")
        .WithEndpoint(name: "mcp", port: filesystemMcpPort, targetPort: 8793)
        .WithBindMount(source: repoRoot, target: "/data", isReadOnly: true)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-filesystem");

    mcpServer.WithEnvironment("McpOptions__ExternalConnectorEndpointOverrides__filesystem",
        filesystemMcp.GetEndpoint("mcp"));
}

// Fetch MCP — fetches any URL and returns clean text/markdown. Ideal for pulling
// NuGet docs, Angular docs, MS Learn articles, GitHub READMEs into AI context.
// No auth or configuration needed. Tiny stateless container.
var fetchEnabled = IsEnabled(builder.Configuration, "Projects:Fetch:Enabled");
if (fetchEnabled)
{
    var fetchMcp = builder.AddContainer("fetch-mcp", "mcp/fetch:latest")
        .WithHttpEndpoint(name: "mcp", port: fetchMcpPort, targetPort: 8000)
        .WithEnvironment("HOST", "0.0.0.0")
        .WithEnvironment("PORT", "8000")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-fetch");

    // Pass EndpointReference directly — string interpolation would ToString() to a bogus value.
    // Ryan.MCP's ExternalConnectorRegistry normalizes bare http(s) URLs to …/mcp.
    mcpServer.WithEnvironment("McpOptions__ExternalConnectorEndpointOverrides__fetch",
        fetchMcp.GetEndpoint("mcp"));
}

// Legacy Memory MCP (stdio wrapper) — disabled by default now that postgres memory is native in Ryan.MCP.
var memoryEnabled = IsEnabled(builder.Configuration, "Projects:Memory:Enabled");
if (memoryEnabled)
{
    // supergateway wraps the stdio-based memory server as HTTP/SSE.
    // mcp/memory:latest exits immediately without a stdio client — supergateway keeps it alive.
    // OpenCode / MCP C# SDK expect Streamable HTTP at /mcp by default. Supergateway's default
    // stdio→wire mode is SSE (/sse + /message), which causes "SSE error" in Streamable-HTTP-first clients.
    var memoryMcp = builder.AddContainer("memory-mcp", "ghcr.io/supercorp-ai/supergateway")
        .WithHttpEndpoint(name: "mcp", port: legacyMemoryMcpPort, targetPort: 8000)
        .WithArgs(
            "--port", "8000",
            "--outputTransport", "streamableHttp",
            "--stdio", "npx -y @modelcontextprotocol/server-memory")
        .WithEnvironment("MEMORY_FILE_PATH", "/data/memory.json")
        .WithBindMount(source: memoryDataPath, target: "/data", isReadOnly: false)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-memory");

    mcpServer.WithEnvironment("McpOptions__ExternalConnectorEndpointOverrides__memory",
        memoryMcp.GetEndpoint("mcp"));
}

// Ollama — local LLM inference (very heavy, GPU recommended).
var ollamaEnabled = IsEnabled(builder.Configuration, "Projects:Ollama:Enabled");
if (ollamaEnabled)
{
    builder.AddContainer("ollama", "ollama/ollama:latest")
        .WithHttpEndpoint(port: ollamaPort, targetPort: 11434)
        .WithEnvironment("OLLAMA_HOST", "0.0.0.0:11434")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-ollama");
}

// Qdrant — vector database for semantic search (heavy, enable only if needed).
var qdrantEnabled = IsEnabled(builder.Configuration, "Projects:Qdrant:Enabled");
if (qdrantEnabled)
{
    builder.AddContainer("qdrant", "qdrant/qdrant:latest")
        .WithHttpEndpoint(port: qdrantPort, targetPort: 6333)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerName($"{projectSlug}-qdrant");
}

builder.Build().Run();
