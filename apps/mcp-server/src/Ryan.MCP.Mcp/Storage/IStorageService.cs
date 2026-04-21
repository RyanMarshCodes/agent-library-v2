namespace Ryan.MCP.Mcp.Storage;

public interface IStorageService
{
    Task<(bool Success, string Message, string? Path)> SaveAgentAsync(
        string fileName, Stream content, CancellationToken cancellationToken);

    Task<(bool Success, string Message, string? Path)> SaveDocumentAsync(
        string tier, string fileName, Stream content, CancellationToken cancellationToken);

    (bool Success, string Message) DeleteAgent(string fileName);

    (bool Success, string Message) DeleteDocument(string tier, string relativePath);

    IEnumerable<string> ListAgents();

    IEnumerable<string> ListDocuments(string tier);
}
