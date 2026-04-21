using Microsoft.Extensions.Options;
using Npgsql;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.Memory;

public sealed class MemoryMigrationRunner(
    NpgsqlDataSource dataSource,
    IOptions<McpOptions> options,
    ILogger<MemoryMigrationRunner> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var migrationsDir = Path.Combine(AppContext.BaseDirectory, "Database", "Migrations");
        if (!Directory.Exists(migrationsDir))
        {
            throw new DirectoryNotFoundException($"Migrations directory not found: {migrationsDir}");
        }

        var sqlFiles = Directory.GetFiles(migrationsDir, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sqlFiles.Count == 0)
        {
            logger.LogWarning("No migration files found in {Dir}", migrationsDir);
            return;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                await EnsureDatabaseExistsAsync(cancellationToken).ConfigureAwait(false);

                await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                foreach (var sqlPath in sqlFiles)
                {
                    var sql = await File.ReadAllTextAsync(sqlPath, cancellationToken).ConfigureAwait(false);
                    await using var cmd = new NpgsqlCommand(sql, conn);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Migration applied: {File}", Path.GetFileName(sqlPath));
                }

                return;
            }
            catch (NpgsqlException ex)
            {
                lastError = ex;
                logger.LogWarning(ex, "Migration attempt {Attempt} failed (connection); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + attempt, 5)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Unable to connect to postgres backend after retries.", lastError);
    }

    /// <summary>
    /// Connects to the default 'postgres' database and creates the target database if it doesn't exist.
    /// This handles the case where POSTGRES_DB only applies on first data-directory init but the
    /// PROJECT_SLUG (and therefore DB name) may change between runs.
    /// </summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var cs = options.Value.MemoryStore.ConnectionString;
        var csBuilder = new NpgsqlConnectionStringBuilder(cs);
        var targetDb = csBuilder.Database;

        if (string.IsNullOrEmpty(targetDb) || targetDb.Equals("postgres", StringComparison.OrdinalIgnoreCase))
        {
            return; // already targeting the default DB, nothing to create
        }

        // Connect to the 'postgres' maintenance database to run CREATE DATABASE
        csBuilder.Database = "postgres";
        await using var adminSource = new NpgsqlDataSourceBuilder(csBuilder.ToString()).Build();
        await using var conn = await adminSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var checkCmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @db;", conn);
        checkCmd.Parameters.AddWithValue("db", targetDb);
        var exists = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (exists is null)
        {
            // CREATE DATABASE cannot run inside a transaction and doesn't support parameters.
            // Escape the identifier to prevent SQL injection via database name.
            var safeName = targetDb.Replace("\"", "\"\"");
            await using var createCmd = new NpgsqlCommand(
                $"CREATE DATABASE \"{safeName}\";", conn);
            await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Created database '{Database}'", targetDb);
        }
    }
}
