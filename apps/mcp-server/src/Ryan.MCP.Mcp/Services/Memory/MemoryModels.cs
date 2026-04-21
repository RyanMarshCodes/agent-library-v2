namespace Ryan.MCP.Mcp.Services.Memory;

public sealed record MemoryEntityRecord(string Name, string EntityType, List<string> Observations);

public sealed record MemoryRelationRecord(string From, string To, string RelationType);

public sealed record MemoryGraphRecord(
    IReadOnlyList<MemoryEntityRecord> Entities,
    IReadOnlyList<MemoryRelationRecord> Relations);
