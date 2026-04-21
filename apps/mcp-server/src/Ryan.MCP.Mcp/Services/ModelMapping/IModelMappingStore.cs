namespace Ryan.MCP.Mcp.Services.ModelMapping;

public interface IModelMappingStore
{
    Task<AgentModelMapping?> GetAsync(string agentName, CancellationToken ct = default);
    Task<IReadOnlyList<AgentModelMapping>> ListAsync(string? tier = null, CancellationToken ct = default);
    Task UpsertAsync(AgentModelMapping mapping, CancellationToken ct = default);
    Task BulkUpsertAsync(IReadOnlyList<AgentModelMapping> mappings, bool preserveManual = true, CancellationToken ct = default);
    Task DeleteAsync(string agentName, CancellationToken ct = default);
}
