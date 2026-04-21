using Npgsql;

namespace Ryan.MCP.Mcp.Services.Memory;

public sealed class PostgresMemoryStore(
    NpgsqlDataSource dataSource,
    ILogger<PostgresMemoryStore> logger) : IMemoryStore
{
    public async Task<IReadOnlyList<MemoryEntityRecord>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        var q = string.IsNullOrWhiteSpace(query) ? string.Empty : query.Trim();
        maxResults = Math.Clamp(maxResults, 1, 50);
        var entities = new List<MemoryEntityRecord>();

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string entitySql = """
            SELECT e.name, e.entity_type
            FROM memory_entities e
            WHERE e.name ILIKE ('%' || @q || '%')
               OR EXISTS (
                    SELECT 1 FROM memory_observations o
                    WHERE o.entity_name = e.name
                      AND o.content ILIKE ('%' || @q || '%')
               )
            ORDER BY e.updated_at DESC
            LIMIT @limit;
            """;

        await using var cmd = new NpgsqlCommand(entitySql, conn);
        cmd.Parameters.AddWithValue("q", q);
        cmd.Parameters.AddWithValue("limit", maxResults);

        var names = new List<(string Name, string Type)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (name, type) in names)
        {
            const string obsSql = """
                SELECT content
                FROM memory_observations
                WHERE entity_name = @name
                ORDER BY id DESC
                LIMIT 5;
                """;
            await using var obsCmd = new NpgsqlCommand(obsSql, conn);
            obsCmd.Parameters.AddWithValue("name", name);
            var observations = new List<string>();
            await using var obsReader = await obsCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await obsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                observations.Add(obsReader.GetString(0));
            }

            entities.Add(new MemoryEntityRecord(name, type, observations));
        }

        return entities;
    }

    public async Task UpsertEntityAsync(string entityName, string entityType, IReadOnlyList<string> observations, CancellationToken cancellationToken = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string upsertEntity = """
            INSERT INTO memory_entities(name, entity_type)
            VALUES (@name, @type)
            ON CONFLICT (name) DO UPDATE
            SET entity_type = EXCLUDED.entity_type,
                updated_at = NOW();
            """;
        await using (var cmd = new NpgsqlCommand(upsertEntity, conn, tx))
        {
            cmd.Parameters.AddWithValue("name", entityName);
            cmd.Parameters.AddWithValue("type", entityType);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string insertObs = """
            INSERT INTO memory_observations(entity_name, content)
            SELECT @name, @content
            WHERE NOT EXISTS (
                SELECT 1
                FROM memory_observations
                WHERE entity_name = @name
                  AND content = @content
            );
            """;
        foreach (var obs in observations.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            await using var cmd = new NpgsqlCommand(insertObs, conn, tx);
            cmd.Parameters.AddWithValue("name", entityName);
            cmd.Parameters.AddWithValue("content", obs.Trim());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateRelationAsync(string fromEntity, string toEntity, string relationType, CancellationToken cancellationToken = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            INSERT INTO memory_relations(from_entity_name, to_entity_name, relation_type)
            VALUES (@from, @to, @type)
            ON CONFLICT (from_entity_name, to_entity_name, relation_type) DO NOTHING;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("from", fromEntity);
        cmd.Parameters.AddWithValue("to", toEntity);
        cmd.Parameters.AddWithValue("type", relationType);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryGraphRecord> ReadGraphAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string eSql = """
            SELECT name, entity_type
            FROM memory_entities
            ORDER BY updated_at DESC;
            """;
        var entities = new List<MemoryEntityRecord>();
        var names = new List<(string Name, string Type)>();
        await using (var eCmd = new NpgsqlCommand(eSql, conn))
        await using (var eReader = await eCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await eReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add((eReader.GetString(0), eReader.GetString(1)));
            }
        }

        foreach (var (name, type) in names)
        {
            const string oSql = """
                SELECT content
                FROM memory_observations
                WHERE entity_name = @name
                ORDER BY id;
                """;
            var observations = new List<string>();
            await using var oCmd = new NpgsqlCommand(oSql, conn);
            oCmd.Parameters.AddWithValue("name", name);
            await using var oReader = await oCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await oReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                observations.Add(oReader.GetString(0));
            }

            entities.Add(new MemoryEntityRecord(name, type, observations));
        }

        const string rSql = """
            SELECT from_entity_name, to_entity_name, relation_type
            FROM memory_relations
            ORDER BY id;
            """;
        var relations = new List<MemoryRelationRecord>();
        await using (var rCmd = new NpgsqlCommand(rSql, conn))
        await using (var rReader = await rCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await rReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                relations.Add(new MemoryRelationRecord(
                    rReader.GetString(0),
                    rReader.GetString(1),
                    rReader.GetString(2)));
            }
        }

        return new MemoryGraphRecord(entities, relations);
    }

    public async Task<(bool Available, string? Message)> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Postgres memory store unavailable");
            return (false, ex.Message);
        }
    }
}
