using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Storage;

public sealed class AzureBlobStorageService : IStorageService
{
    private readonly McpOptions _options;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<AzureBlobStorageService> _logger;

    public AzureBlobStorageService(McpOptions options, ILogger<AzureBlobStorageService> logger)
    {
        _options = options;
        _logger = logger;

        var connectionString = _options.Storage.ConnectionString
            ?? throw new InvalidOperationException("Azure Storage connection string not configured.");
        var containerName = _options.Storage.ContainerName;

        _blobClient = new BlobServiceClient(connectionString);
        var containerClient = _blobClient.GetBlobContainerClient(containerName);
        containerClient.CreateIfNotExists(PublicAccessType.None);
    }

    private BlobContainerClient GetContainer() => _blobClient.GetBlobContainerClient(_options.Storage.ContainerName);

    private string GetBlobPath(string tier, string fileName) => $"{tier}/{SanitizeFileName(fileName)}";

    public async Task<(bool Success, string Message, string? Path)> SaveAgentAsync(
        string fileName, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            var container = GetContainer();
            var blobName = $"agents/{SanitizeFileName(fileName)}";
            var blobClient = container.GetBlobClient(blobName);

            await blobClient.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "text/markdown" }
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Agent uploaded to Azure Blob: {BlobName}", blobName);
            return (true, $"Agent '{fileName}' uploaded successfully.", blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload agent: {FileName}", fileName);
            return (false, $"Upload failed: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message, string? Path)> SaveDocumentAsync(
        string tier, string fileName, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            var container = GetContainer();
            var blobName = GetBlobPath(tier, fileName);
            var blobClient = container.GetBlobClient(blobName);

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var contentType = extension switch
            {
                ".md" => "text/markdown",
                ".json" => "application/json",
                ".yaml" or ".yml" => "text/yaml",
                ".txt" => "text/plain",
                _ => "application/octet-stream"
            };

            await blobClient.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Document uploaded to Azure Blob: {BlobName}", blobName);
            return (true, $"Document '{fileName}' uploaded to tier '{tier}'.", blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload document: {FileName} to tier {Tier}", fileName, tier);
            return (false, $"Upload failed: {ex.Message}", null);
        }
    }

    public (bool Success, string Message) DeleteAgent(string fileName)
    {
        try
        {
            var container = GetContainer();
            var blobName = $"agents/{SanitizeFileName(fileName)}";
            var blobClient = container.GetBlobClient(blobName);

            if (!blobClient.Exists())
                return (false, $"Agent '{fileName}' not found.");

            blobClient.Delete();
            _logger.LogInformation("Agent deleted from Azure Blob: {BlobName}", blobName);
            return (true, $"Agent '{fileName}' deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete agent: {FileName}", fileName);
            return (false, $"Delete failed: {ex.Message}");
        }
    }

    public (bool Success, string Message) DeleteDocument(string tier, string relativePath)
    {
        try
        {
            var container = GetContainer();
            var blobName = $"{tier}/{SanitizeFileName(relativePath)}";
            var blobClient = container.GetBlobClient(blobName);

            if (!blobClient.Exists())
                return (false, $"Document '{relativePath}' not found in tier '{tier}'.");

            blobClient.Delete();
            _logger.LogInformation("Document deleted from Azure Blob: {BlobName}", blobName);
            return (true, $"Document '{relativePath}' deleted from tier '{tier}'.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete document: {RelativePath} from tier {Tier}", relativePath, tier);
            return (false, $"Delete failed: {ex.Message}");
        }
    }

    public IEnumerable<string> ListAgents()
    {
        var container = GetContainer();
        return container.GetBlobs(prefix: "agents/")
            .Select(b => b.Name.Substring("agents/".Length));
    }

    public IEnumerable<string> ListDocuments(string tier)
    {
        var container = GetContainer();
        return container.GetBlobs(prefix: $"{tier}/")
            .Select(b => b.Name.Substring($"{tier}/".Length));
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Where(c => !invalid.Contains(c)));
    }
}
