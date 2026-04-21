using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services;

/// <summary>
/// Coordinates ingestion of agent definitions from various file formats and locations.
/// </summary>
public sealed class AgentIngestionCoordinator : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] AgentFilePatterns = [
        "*.agent.md",
        "*.instructions.md",
        "*.prompt.md",
        "*.skill.md",
        "copilot-instructions.md",
        "AGENTS.md",
    ];

    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<AgentIngestionCoordinator> _logger;
    private readonly McpOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _watcherLock = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    private Timer? _debounceTimer;
    private AgentSnapshot _snapshot = AgentSnapshot.Empty;
    private bool _disposed;

    public AgentIngestionCoordinator(
        IOptions<McpOptions> options,
        IWebHostEnvironment environment,
        ILogger<AgentIngestionCoordinator> logger,
        IHostApplicationLifetime appLifetime)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    /// <summary>
    /// Gets the current agent snapshot.
    /// </summary>
    public AgentSnapshot Snapshot => _snapshot;

    /// <summary>
    /// Triggers a reindex of all agents.
    /// </summary>
    public async Task TriggerReindexAsync(CancellationToken cancellationToken)
    {
        await RebuildIndexAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets an agent by name.
    /// </summary>
    public AgentEntry? GetAgent(string name)
    {
        return _snapshot.Agents.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lists agents filtered by scope and/or tags.
    /// </summary>
    public List<AgentEntry> ListAgents(string? scope = null, List<string>? tags = null)
    {
        var result = _snapshot.Agents.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(scope))
        {
            result = result.Where(x =>
                string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase));
        }

        if (tags?.Count > 0)
        {
            result = result.Where(x =>
                tags.Any(tag =>
                    x.Tags.Any(t =>
                        string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))));
        }

        return result.ToList();
    }

    /// <summary>
    /// Searches agents by free-text query against name, description, tags, relative path, and filename.
    /// Returns results sorted by relevance (name matches > relative path > description > tags).
    /// </summary>
    public List<AgentEntry> SearchAgents(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = query.ToLowerInvariant()
            .Split([' ', '-', '_', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToList();

        if (terms.Count == 0)
        {
            return [];
        }

        var scored = _snapshot.Agents
            .Select(agent =>
            {
                var nameLower = agent.Name.ToLowerInvariant();
                var descLower = agent.Description.ToLowerInvariant();
                var pathLower = agent.RelativePath.ToLowerInvariant();
                var filenameLower = agent.FileName.ToLowerInvariant();
                var tagsLower = agent.Tags.Select(t => t.ToLowerInvariant()).ToList();

                var score = 0;

                // Name matches: highest weight
                foreach (var term in terms)
                {
                    if (nameLower.Contains(term))
                        score += 100;
                }

                // Relative path matches
                foreach (var term in terms)
                {
                    if (pathLower.Contains(term) || filenameLower.Contains(term))
                        score += 50;
                }

                // Description matches
                foreach (var term in terms)
                {
                    if (descLower.Contains(term))
                        score += 20;
                }

                // Tag matches
                foreach (var term in terms)
                {
                    if (tagsLower.Any(t => t.Contains(term)))
                        score += 10;
                }

                return (Agent: agent, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Agent.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Agent)
            .ToList();

        return scored;
    }

    /// <summary>
    /// Recommends agents for a given task with scoring and reasoning.
    /// </summary>
    public List<(AgentEntry Agent, int Score, List<string> Reasons)> RecommendAgents(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return [];
        }

        var taskLower = task.ToLowerInvariant();
        var taskTerms = taskLower
            .Split([' ', '-', '_', '/', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToList();

        var keywords = new Dictionary<string, string[]>
        {
            ["security"] = ["security", "audit", "vulnerability", "owasp", "penetration"],
            ["refactoring"] = ["refactor", "simplify", "cleanup", "restructure", "tech-debt"],
            ["testing"] = ["test", "spec", "verify", "coverage", "unittest", "integration"],
            ["api"] = ["api", "rest", "graphql", "endpoint", "openapi", "swagger"],
            ["database"] = ["database", "sql", "migration", "schema", "query", "postgres"],
            ["frontend"] = ["frontend", "ui", "react", "vue", "angular", "css", "component"],
            ["backend"] = ["backend", "server", "service", "microservice", "dotnet", "node"],
            ["devops"] = ["devops", "docker", "kubernetes", "ci", "cd", "deploy", "azure"],
            ["game"] = ["game", "unity", "unreal", "gaming", "narrative", "design"],
            ["documentation"] = ["docs", "documentation", "readme", "guide", "api-doc"],
            ["architecture"] = ["architecture", "design", "pattern", "microservices", "system"],
        };

        var scored = _snapshot.Agents
            .Select(agent =>
            {
                var score = 0;
                var reasons = new List<string>();
                var nameLower = agent.Name.ToLowerInvariant();
                var descLower = agent.Description.ToLowerInvariant();
                var scopeLower = agent.Scope.ToLowerInvariant();
                var tagsLower = agent.Tags.Select(t => t.ToLowerInvariant()).ToList();
                var pathLower = agent.RelativePath.ToLowerInvariant();

                // Direct name/description match
                foreach (var term in taskTerms)
                {
                    if (nameLower.Contains(term))
                    {
                        score += 50;
                        reasons.Add($"name matches '{term}'");
                    }
                }

                foreach (var term in taskTerms)
                {
                    if (descLower.Contains(term))
                    {
                        score += 25;
                        reasons.Add($"description mentions '{term}'");
                    }
                }

                // Keyword-based scoring
                foreach (var (category, keywords) in keywords)
                {
                    var taskHasKeyword = taskTerms.Any(t => keywords.Any(k => k.Contains(t) || t.Contains(k)));
                    var agentMatches = tagsLower.Any(t => keywords.Any(k => k.Contains(t) || t.Contains(k))) ||
                                      scopeLower.Contains(category) ||
                                      nameLower.Contains(category);

                    if (taskHasKeyword && agentMatches)
                    {
                        score += 30;
                        reasons.Add($"relevant to {category}");
                    }
                }

                // Scope match
                foreach (var term in taskTerms)
                {
                    if (scopeLower.Contains(term))
                    {
                        score += 40;
                        reasons.Add($"scope '{agent.Scope}' matches");
                    }
                }

                // Tag match
                foreach (var term in taskTerms)
                {
                    var matchingTag = tagsLower.FirstOrDefault(t => t.Contains(term));
                    if (matchingTag != null)
                    {
                        score += 20;
                        reasons.Add($"tag '{matchingTag}' matches");
                    }
                }

                // Path/folder match
                foreach (var term in taskTerms)
                {
                    if (pathLower.Contains(term))
                    {
                        score += 15;
                        reasons.Add($"located in relevant folder");
                    }
                }

                // Boost for scope match (exact)
                if (IsScopeRelevant(taskLower, agent.Scope))
                {
                    score += 20;
                }

                return (Agent: agent, Score: score, Reasons: reasons);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Agent.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return scored;
    }

    private static bool IsScopeRelevant(string task, string scope)
    {
        var taskWords = task.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var scopeKeywords = new Dictionary<string, string[]>
        {
            ["security-audit"] = ["security", "audit", "vulnerability", "secure", "penetration"],
            ["refactoring"] = ["refactor", "simplify", "cleanup", "refactor"],
            ["testing"] = ["test", "spec", "verify", "coverage", "unit", "integration"],
            ["api-design"] = ["api", "rest", "graphql", "endpoint", "openapi"],
            ["data"] = ["database", "sql", "schema", "query", "migration"],
            ["frontend"] = ["frontend", "ui", "component", "css", "react", "vue", "angular"],
            ["backend"] = ["backend", "server", "service", "api"],
            ["devops"] = ["devops", "deploy", "ci", "cd", "docker", "kubernetes"],
            ["game-design"] = ["game", "gaming", "design", "narrative", "level"],
            ["documentation"] = ["docs", "documentation", "readme", "write"],
            ["architecture"] = ["architecture", "design", "system", "pattern"],
        };

        if (scopeKeywords.TryGetValue(scope, out var keywords))
        {
            return taskWords.Any(t => keywords.Any(k => k.Contains(t) || t.Contains(k)));
        }

        return false;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Ingestion.AutoIngestOnStartup)
        {
            await RebuildIndexAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_options.Ingestion.WatchForChanges)
        {
            InitializeWatchers();
        }

        _appLifetime.ApplicationStopping.Register(Dispose);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }

        lock (_watcherLock)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _indexLock.Dispose();
        _disposed = true;
    }

    private static string? ResolvePath(string contentRoot, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRoot, configuredPath));
    }

    private Dictionary<string, string> GetScanRoots()
    {
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = ResolvePath(_environment.ContentRootPath, _options.Agents?.LocalPath),
            ["library"] = ResolvePath(_environment.ContentRootPath, _options.Agents?.LibraryPath),
            ["project"] = ResolvePath(_environment.ContentRootPath, _options.Agents?.ProjectPath),
        };

        // Also add repo root for project-level instructions
        var repoRoot = _environment.ContentRootPath;
        roots["repo-root"] = repoRoot;

        return roots
            .Where(x => !string.IsNullOrWhiteSpace(x.Value) && (
                Directory.Exists(x.Value) ||
                (x.Key == "repo-root" && Directory.Exists(x.Value))))
            .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RebuildIndexAsync(CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedUtc = DateTime.UtcNow;
            var scanRoots = GetScanRoots();
            var entries = new List<AgentEntry>();

            foreach (var (rootName, rootPath) in scanRoots)
            {
                // For repo root, only look for specific files at the root level
                if (rootName == "repo-root")
                {
                    foreach (var pattern in AgentFilePatterns)
                    {
                        var files = Directory.EnumerateFiles(rootPath, pattern, SearchOption.TopDirectoryOnly);
                        foreach (var file in files)
                        {
                            var entry = await IngestionEntryAsync(file, rootPath, cancellationToken).ConfigureAwait(false);
                            if (entry != null)
                            {
                                entries.Add(entry);
                            }
                        }
                    }
                }
                else
                {
                    // For named directories, search recursively
                    foreach (var pattern in AgentFilePatterns)
                    {
                        var files = Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            var entry = await IngestionEntryAsync(file, rootPath, cancellationToken).ConfigureAwait(false);
                            if (entry != null)
                            {
                                entries.Add(entry);
                            }
                        }
                    }
                }
            }

            var byScope = entries
                .GroupBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

            var byFormat = entries
                .GroupBy(x => x.Format, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

            _snapshot = new AgentSnapshot
            {
                ProjectSlug = _options.Knowledge.ProjectSlug,
                UpdatedUtc = DateTime.UtcNow,
                LastStartedUtc = startedUtc,
                TotalAgents = entries.Count,
                ByScope = byScope,
                ByFormat = byFormat,
                ScanRoots = scanRoots,
                Agents = entries
                    .OrderBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };

            await PersistSnapshotAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "MCP agent ingestion indexed {Count} agent(s): {Breakdown}",
                entries.Count,
                string.Join(", ", byScope.Select(x => $"{x.Key}={x.Value}")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild MCP agent ingestion index.");
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<AgentEntry?> IngestionEntryAsync(string filePath, string rootPath, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            var checksum = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);

            var parseResult = YamlFrontmatterParser.TryParseFrontmatter(content);
            var frontmatter = parseResult.Frontmatter;

            var format = AgentFormatDetector.DetectFormat(fileInfo.Name, frontmatter);
            var name = YamlFrontmatterParser.GetString(frontmatter, "name") ??
                      Path.GetFileNameWithoutExtension(fileInfo.Name);
            var description = YamlFrontmatterParser.GetString(frontmatter, "description", string.Empty) ?? string.Empty;
            var scope = YamlFrontmatterParser.GetString(frontmatter, "scope", "general") ?? "general";
            var tags = YamlFrontmatterParser.GetStringList(frontmatter, "tags");

            var relativePath = Path.GetRelativePath(rootPath, filePath);

            return new AgentEntry
            {
                FileName = fileInfo.Name,
                Name = name,
                Description = description,
                Scope = scope,
                Tags = tags,
                Format = format,
                RawContent = content,
                Frontmatter = frontmatter,
                AbsolutePath = filePath,
                RelativePath = relativePath,
                ChecksumSha256 = checksum,
                IndexedUtc = DateTime.UtcNow,
                SizeBytes = fileInfo.Length,
                LastWriteUtc = fileInfo.LastWriteTimeUtc,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ingest agent file: {FilePath}", filePath);
            return null;
        }
    }

    private async Task PersistSnapshotAsync(CancellationToken cancellationToken)
    {
        var indexPath = ResolvePath(_environment.ContentRootPath, _options.Knowledge.IndexPath);
        if (string.IsNullOrWhiteSpace(indexPath))
        {
            return;
        }

        Directory.CreateDirectory(indexPath);
        var outputPath = Path.Combine(indexPath, "agents-index.json");
        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, _snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private void InitializeWatchers()
    {
        lock (_watcherLock)
        {
            foreach (var (rootName, rootPath) in GetScanRoots())
            {
                var watcher = new FileSystemWatcher(rootPath)
                {
                    IncludeSubdirectories = rootName != "repo-root",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };

                watcher.Changed += OnWatchedFileChanged;
                watcher.Created += OnWatchedFileChanged;
                watcher.Deleted += OnWatchedFileChanged;
                watcher.Renamed += OnWatchedFileChanged;
                watcher.Error += (_, ex) =>
                    _logger.LogWarning(ex.GetException(), "Agent ingestion watcher error for {RootName}.", rootName);

                _watchers.Add(watcher);
                _logger.LogInformation("Agent ingestion watcher enabled for {RootName} at {RootPath}.", rootName, rootPath);
            }
        }
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
    {
        var fileName = Path.GetFileName(e.FullPath);
        var isAgentFile = AgentFilePatterns.Any(pattern =>
            fileName.Equals(pattern.Replace("*", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase) ||
            (pattern.Contains('*', StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(pattern.Replace("*", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase)));

        if (!isAgentFile)
        {
            return;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(
            _ => TriggerDebouncedReindex(),
            null,
            _options.Ingestion.DebounceMilliseconds,
            Timeout.Infinite);
    }

    private void TriggerDebouncedReindex()
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RebuildIndexAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Debounced agent ingestion reindex failed.");
                }
            });
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
