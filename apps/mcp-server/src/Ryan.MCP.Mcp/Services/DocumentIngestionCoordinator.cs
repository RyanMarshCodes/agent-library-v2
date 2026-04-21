using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services;

public sealed class DocumentIngestionCoordinator : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<DocumentIngestionCoordinator> _logger;
    private readonly McpOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _watcherLock = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    private Timer? _debounceTimer;
    private IngestionSnapshot _snapshot = IngestionSnapshot.Empty;
    private bool _disposed;

    public DocumentIngestionCoordinator(
        IOptions<McpOptions> options,
        IWebHostEnvironment environment,
        ILogger<DocumentIngestionCoordinator> logger,
        IHostApplicationLifetime appLifetime)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public IngestionSnapshot Snapshot => _snapshot;

    public async Task TriggerReindexAsync(CancellationToken cancellationToken)
    {
        await RebuildIndexAsync(cancellationToken).ConfigureAwait(false);
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

    private Dictionary<string, string> GetTierRoots()
    {
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["official"] = ResolvePath(_environment.ContentRootPath, _options.Knowledge.OfficialPath),
            ["organization"] = ResolvePath(_environment.ContentRootPath, _options.Knowledge.OrganizationPath),
            ["project"] = ResolvePath(_environment.ContentRootPath, _options.Knowledge.ProjectPath),
        };

        return roots
            .Where(x => !string.IsNullOrWhiteSpace(x.Value) && Directory.Exists(x.Value))
            .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RebuildIndexAsync(CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedUtc = DateTime.UtcNow;
            var roots = GetTierRoots();
            var allowedExtensions = new HashSet<string>(
                _options.Ingestion.AllowedExtensions.Select(x => x.StartsWith('.') ? x : "." + x),
                StringComparer.OrdinalIgnoreCase);

            var entries = new List<IngestionEntry>();
            foreach (var (tier, root) in roots)
            {
                var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(file => allowedExtensions.Contains(Path.GetExtension(file)));

                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    var checksum = await ComputeSha256Async(file, cancellationToken).ConfigureAwait(false);
                    entries.Add(new IngestionEntry
                    {
                        Tier = tier,
                        AbsolutePath = file,
                        RelativePath = Path.GetRelativePath(root, file),
                        SizeBytes = fileInfo.Length,
                        LastWriteUtc = fileInfo.LastWriteTimeUtc,
                        ChecksumSha256 = checksum,
                        IndexedUtc = DateTime.UtcNow,
                    });
                }
            }

            var byTier = entries
                .GroupBy(x => x.Tier, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

            _snapshot = new IngestionSnapshot
            {
                ProjectSlug = _options.Knowledge.ProjectSlug,
                UpdatedUtc = DateTime.UtcNow,
                LastStartedUtc = startedUtc,
                TotalDocuments = entries.Count,
                ByTier = byTier,
                Roots = roots,
                Documents = entries.OrderBy(x => x.Tier, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };

            await PersistSnapshotAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("MCP ingestion indexed {Count} document(s).", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild MCP ingestion index.");
        }
        finally
        {
            _indexLock.Release();
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
        var outputPath = Path.Combine(indexPath, "standards-index.json");
        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, _snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private void InitializeWatchers()
    {
        lock (_watcherLock)
        {
            foreach (var (tier, root) in GetTierRoots())
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };

                watcher.Changed += OnWatchedFileChanged;
                watcher.Created += OnWatchedFileChanged;
                watcher.Deleted += OnWatchedFileChanged;
                watcher.Renamed += OnWatchedFileChanged;
                watcher.Error += (_, ex) => _logger.LogWarning(ex.GetException(), "Ingestion watcher error for tier {Tier}.", tier);

                _watchers.Add(watcher);
                _logger.LogInformation("Ingestion watcher enabled for {Tier} at {Root}.", tier, root);
            }
        }
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
    {
        var extension = Path.GetExtension(e.FullPath);
        if (!_options.Ingestion.AllowedExtensions.Any(
                x => string.Equals(
                    x.StartsWith('.') ? x : "." + x,
                    extension,
                    StringComparison.OrdinalIgnoreCase)))
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
                    _logger.LogError(ex, "Debounced ingestion reindex failed.");
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

public class IngestionSnapshot
{
    public static IngestionSnapshot Empty { get; } = new()
    {
        ProjectSlug = string.Empty,
        UpdatedUtc = DateTime.UtcNow,
        LastStartedUtc = DateTime.UtcNow,
        TotalDocuments = 0,
        ByTier = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        Roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Documents = [],
    };

    public string ProjectSlug { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public DateTime LastStartedUtc { get; set; }
    public int TotalDocuments { get; set; }
    public Dictionary<string, int> ByTier { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<IngestionEntry> Documents { get; set; } = [];
}

public class IngestionEntry
{
    public string Tier { get; set; } = string.Empty;
    public string AbsolutePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string ChecksumSha256 { get; set; } = string.Empty;
    public DateTime IndexedUtc { get; set; }
}
