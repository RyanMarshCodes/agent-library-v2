namespace Ryan.MCP.Mcp.Services.Memory;

public interface IMemoryStore
{
    Task<IReadOnlyList<MemoryEntityRecord>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default);
    Task UpsertEntityAsync(string entityName, string entityType, IReadOnlyList<string> observations, CancellationToken cancellationToken = default);
    Task CreateRelationAsync(string fromEntity, string toEntity, string relationType, CancellationToken cancellationToken = default);
    Task<MemoryGraphRecord> ReadGraphAsync(CancellationToken cancellationToken = default);
    Task<(bool Available, string? Message)> CheckAvailabilityAsync(CancellationToken cancellationToken = default);
}
