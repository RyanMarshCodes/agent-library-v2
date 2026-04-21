using Npgsql;

namespace Ryan.MCP.Mcp.Services.ModelMapping;

public sealed class PostgresModelMappingStore(
    NpgsqlDataSource dataSource,
    ILogger<PostgresModelMappingStore> logger) : IModelMappingStore
{
    public async Task<AgentModelMapping?> GetAsync(string agentName, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        const string sql = """
            SELECT agent_name, tier, primary_model, primary_provider, tool_overrides_json,
                   alt_model_1, alt_provider_1, alt_model_2, alt_provider_2,
                   cost_per_1m_in, cost_per_1m_out, notes, synced_from
            FROM agent_model_mappings
            WHERE agent_name = @name;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("name", agentName);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader) : null;
    }

    public async Task<IReadOnlyList<AgentModelMapping>> ListAsync(string? tier = null, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = tier is not null
            ? """
                            SELECT agent_name, tier, primary_model, primary_provider, tool_overrides_json,
                     alt_model_1, alt_provider_1, alt_model_2, alt_provider_2,
                     cost_per_1m_in, cost_per_1m_out, notes, synced_from
              FROM agent_model_mappings
              WHERE tier = @tier
              ORDER BY agent_name;
              """
            : """
                            SELECT agent_name, tier, primary_model, primary_provider, tool_overrides_json,
                     alt_model_1, alt_provider_1, alt_model_2, alt_provider_2,
                     cost_per_1m_in, cost_per_1m_out, notes, synced_from
              FROM agent_model_mappings
              ORDER BY tier, agent_name;
              """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (tier is not null)
        {
            cmd.Parameters.AddWithValue("tier", tier);
        }

        var results = new List<AgentModelMapping>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadRow(reader));
        }

        return results;
    }

    public async Task UpsertAsync(AgentModelMapping mapping, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO agent_model_mappings
                (agent_name, tier, primary_model, primary_provider, tool_overrides_json,
                 alt_model_1, alt_provider_1, alt_model_2, alt_provider_2,
                 cost_per_1m_in, cost_per_1m_out, notes, synced_from, updated_at)
            VALUES
                (@agent_name, @tier, @primary_model, @primary_provider, @tool_overrides_json,
                 @alt_model_1, @alt_provider_1, @alt_model_2, @alt_provider_2,
                 @cost_in, @cost_out, @notes, @synced_from, NOW())
            ON CONFLICT (agent_name) DO UPDATE SET
                tier = EXCLUDED.tier,
                primary_model = EXCLUDED.primary_model,
                primary_provider = EXCLUDED.primary_provider,
                tool_overrides_json = EXCLUDED.tool_overrides_json,
                alt_model_1 = EXCLUDED.alt_model_1,
                alt_provider_1 = EXCLUDED.alt_provider_1,
                alt_model_2 = EXCLUDED.alt_model_2,
                alt_provider_2 = EXCLUDED.alt_provider_2,
                cost_per_1m_in = EXCLUDED.cost_per_1m_in,
                cost_per_1m_out = EXCLUDED.cost_per_1m_out,
                notes = EXCLUDED.notes,
                synced_from = EXCLUDED.synced_from,
                updated_at = NOW();
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        AddMappingParams(cmd, mapping);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task BulkUpsertAsync(IReadOnlyList<AgentModelMapping> mappings, bool preserveManual = true, CancellationToken ct = default)
    {
        if (mappings.Count == 0)
        {
            return;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // If preserving manual overrides, collect them first
        var manualAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (preserveManual)
        {
            const string manualSql = "SELECT agent_name FROM agent_model_mappings WHERE synced_from = 'manual';";
            await using var manualCmd = new NpgsqlCommand(manualSql, conn, tx);
            await using var reader = await manualCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                manualAgents.Add(reader.GetString(0));
            }
        }

        const string sql = """
            INSERT INTO agent_model_mappings
                (agent_name, tier, primary_model, primary_provider, tool_overrides_json,
                 alt_model_1, alt_provider_1, alt_model_2, alt_provider_2,
                 cost_per_1m_in, cost_per_1m_out, notes, synced_from, updated_at)
            VALUES
                (@agent_name, @tier, @primary_model, @primary_provider, @tool_overrides_json,
                 @alt_model_1, @alt_provider_1, @alt_model_2, @alt_provider_2,
                 @cost_in, @cost_out, @notes, @synced_from, NOW())
            ON CONFLICT (agent_name) DO UPDATE SET
                tier = EXCLUDED.tier,
                primary_model = EXCLUDED.primary_model,
                primary_provider = EXCLUDED.primary_provider,
                tool_overrides_json = EXCLUDED.tool_overrides_json,
                alt_model_1 = EXCLUDED.alt_model_1,
                alt_provider_1 = EXCLUDED.alt_provider_1,
                alt_model_2 = EXCLUDED.alt_model_2,
                alt_provider_2 = EXCLUDED.alt_provider_2,
                cost_per_1m_in = EXCLUDED.cost_per_1m_in,
                cost_per_1m_out = EXCLUDED.cost_per_1m_out,
                notes = EXCLUDED.notes,
                synced_from = EXCLUDED.synced_from,
                updated_at = NOW();
            """;

        var synced = 0;
        var skipped = 0;
        foreach (var mapping in mappings)
        {
            if (preserveManual && manualAgents.Contains(mapping.AgentName))
            {
                skipped++;
                continue;
            }

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            AddMappingParams(cmd, mapping);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            synced++;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Bulk upsert: {Synced} synced, {Skipped} manual overrides preserved", synced, skipped);
    }

    public async Task DeleteAsync(string agentName, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        const string sql = "DELETE FROM agent_model_mappings WHERE agent_name = @name;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("name", agentName);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddMappingParams(NpgsqlCommand cmd, AgentModelMapping m)
    {
        cmd.Parameters.AddWithValue("agent_name", m.AgentName);
        cmd.Parameters.AddWithValue("tier", m.Tier);
        cmd.Parameters.AddWithValue("primary_model", m.PrimaryModel);
        cmd.Parameters.AddWithValue("primary_provider", (object?)m.PrimaryProvider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tool_overrides_json", (object?)m.ToolOverridesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("alt_model_1", (object?)m.AltModel1 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("alt_provider_1", (object?)m.AltProvider1 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("alt_model_2", (object?)m.AltModel2 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("alt_provider_2", (object?)m.AltProvider2 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cost_in", (object?)m.CostPer1MIn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cost_out", (object?)m.CostPer1MOut ?? DBNull.Value);
        cmd.Parameters.AddWithValue("notes", (object?)m.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("synced_from", m.SyncedFrom);
    }

    private static AgentModelMapping ReadRow(NpgsqlDataReader reader)
    {
        return new AgentModelMapping(
            AgentName: reader.GetString(0),
            Tier: reader.GetString(1),
            PrimaryModel: reader.GetString(2),
            PrimaryProvider: reader.IsDBNull(3) ? null : reader.GetString(3),
            ToolOverridesJson: reader.IsDBNull(4) ? null : reader.GetString(4),
            AltModel1: reader.IsDBNull(5) ? null : reader.GetString(5),
            AltProvider1: reader.IsDBNull(6) ? null : reader.GetString(6),
            AltModel2: reader.IsDBNull(7) ? null : reader.GetString(7),
            AltProvider2: reader.IsDBNull(8) ? null : reader.GetString(8),
            CostPer1MIn: reader.IsDBNull(9) ? null : reader.GetDecimal(9),
            CostPer1MOut: reader.IsDBNull(10) ? null : reader.GetDecimal(10),
            Notes: reader.IsDBNull(11) ? null : reader.GetString(11),
            SyncedFrom: reader.GetString(12));
    }
}
