using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Storage;

public sealed class FileStorageService : IStorageService
{
    private readonly McpOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(McpOptions options, IWebHostEnvironment environment, ILogger<FileStorageService> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    private string? ResolveAgentLibraryPath() => ResolvePath(_options.Agents?.LibraryPath);
    private string? ResolveLocalAgentPath() => ResolvePath(_options.Agents?.LocalPath);
    private string? ResolveDocumentPath(string tier) => tier switch
    {
        "official" => ResolvePath(_options.Knowledge.OfficialPath),
        "organization" or "org" => ResolvePath(_options.Knowledge.OrganizationPath),
        "project" => ResolvePath(_options.Knowledge.ProjectPath),
        _ => null,
    };

    public async Task<(bool Success, string Message, string? Path)> SaveAgentAsync(
        string fileName, Stream content, CancellationToken cancellationToken)
    {
        var libraryPath = ResolveAgentLibraryPath();
        if (string.IsNullOrWhiteSpace(libraryPath))
            return (false, "Agent library path is not configured.", null);

        Directory.CreateDirectory(libraryPath);
        var safeName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            return (false, "Invalid file name.", null);

        var filePath = Path.Combine(libraryPath, safeName);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent file saved: {FilePath}", filePath);
        return (true, $"Agent '{safeName}' saved successfully.", filePath);
    }

    public async Task<(bool Success, string Message, string? Path)> SaveDocumentAsync(
        string tier, string fileName, Stream content, CancellationToken cancellationToken)
    {
        var basePath = ResolveDocumentPath(tier);
        if (string.IsNullOrWhiteSpace(basePath))
            return (false, $"Document path for tier '{tier}' is not configured.", null);

        Directory.CreateDirectory(basePath);
        var safeName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            return (false, "Invalid file name.", null);

        var filePath = Path.Combine(basePath, safeName);
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Document file saved to {Tier}: {FilePath}", tier, filePath);
        return (true, $"Document '{safeName}' saved to tier '{tier}'.", filePath);
    }

    public (bool Success, string Message) DeleteAgent(string fileName)
    {
        var libraryPath = ResolveAgentLibraryPath();
        if (string.IsNullOrWhiteSpace(libraryPath))
            return (false, "Agent library path is not configured.");

        var safeName = SanitizeFileName(fileName);
        var filePath = Path.Combine(libraryPath, safeName);

        if (!File.Exists(filePath))
        {
            var localPath = ResolveLocalAgentPath();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                var localFilePath = Path.Combine(localPath, safeName);
                if (File.Exists(localFilePath))
                {
                    File.Delete(localFilePath);
                    _logger.LogInformation("Agent file deleted: {FilePath}", localFilePath);
                    return (true, $"Agent '{safeName}' deleted.");
                }
            }
            return (false, $"Agent file '{safeName}' not found.");
        }

        File.Delete(filePath);
        _logger.LogInformation("Agent file deleted: {FilePath}", filePath);
        return (true, $"Agent '{safeName}' deleted.");
    }

    public (bool Success, string Message) DeleteDocument(string tier, string relativePath)
    {
        var basePath = ResolveDocumentPath(tier);
        if (string.IsNullOrWhiteSpace(basePath))
            return (false, $"Document path for tier '{tier}' is not configured.");

        var normalized = Path.GetFullPath(Path.Combine(basePath, relativePath));
        if (!normalized.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return (false, "Invalid path.");

        if (!File.Exists(normalized))
            return (false, $"Document '{relativePath}' not found in tier '{tier}'.");

        File.Delete(normalized);
        _logger.LogInformation("Document deleted from {Tier}: {FilePath}", tier, normalized);
        return (true, $"Document '{relativePath}' deleted from tier '{tier}'.");
    }

    public IEnumerable<string> ListAgents()
    {
        var libraryPath = ResolveAgentLibraryPath();
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return [];

        return Directory.GetFiles(libraryPath, "*.md")
            .Select(Path.GetFileName)
            .Where(f => f != null)!;
    }

    public IEnumerable<string> ListDocuments(string tier)
    {
        var basePath = ResolveDocumentPath(tier);
        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            return [];

        return Directory.GetFiles(basePath, "*.*", SearchOption.AllDirectories)
            .Select(f => f.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar));
    }

    private string? ResolvePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Where(c => !invalid.Contains(c)));
    }
}
