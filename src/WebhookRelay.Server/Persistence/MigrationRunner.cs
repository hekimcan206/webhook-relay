using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace WebhookRelay.Server.Persistence;

/// <summary>
/// Applies the embedded, numbered SQL scripts in order, each in its own transaction. Plain SQL instead of an ORM
/// migration tool: the schema relies on database features (append-only triggers, check constraints,
/// privilege changes) that are clearer written out than generated.
/// </summary>
internal sealed partial class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger)
{
    // Session-level advisory lock: several migrator instances started together apply each script exactly once.
    private const long MigrationLockKey = 0x5245_4C41_5931; // "RELAY1"

    internal sealed record Migration(int Version, string Name, string Sql, string Sha256);

    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, null, $"SELECT pg_advisory_lock({MigrationLockKey})", cancellationToken);
        try
        {
            await ExecuteAsync(connection, null, """
                CREATE TABLE IF NOT EXISTS relay_admin.schema_migrations (
                    version    integer     PRIMARY KEY,
                    name       text        NOT NULL,
                    sha256     text        NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now())
                """, cancellationToken);

            var applied = new Dictionary<int, string>();
            await using (var cmd = new NpgsqlCommand("SELECT version, sha256 FROM relay_admin.schema_migrations", connection))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied[reader.GetInt32(0)] = reader.GetString(1);
                }
            }

            var count = 0;
            foreach (var migration in Load())
            {
                if (applied.TryGetValue(migration.Version, out var checksum))
                {
                    // An applied script must never change: the database would silently differ from the source.
                    if (!string.Equals(checksum, migration.Sha256, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Migration {migration.Version:D4}_{migration.Name} changed after it was applied. Add a new migration instead.");
                    }

                    continue;
                }

                await using var tx = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, tx, migration.Sql, cancellationToken);
                await using (var insert = new NpgsqlCommand(
                    "INSERT INTO relay_admin.schema_migrations (version, name, sha256) VALUES ($1, $2, $3)", connection, tx))
                {
                    insert.Parameters.Add(new() { Value = migration.Version });
                    insert.Parameters.Add(new() { Value = migration.Name });
                    insert.Parameters.Add(new() { Value = migration.Sha256 });
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
                LogApplied(migration.Version, migration.Name);
                count++;
            }

            return count;
        }
        finally
        {
            await ExecuteAsync(connection, null, $"SELECT pg_advisory_unlock({MigrationLockKey})", CancellationToken.None);
        }
    }

    internal static IReadOnlyList<Migration> Load()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("migrations/", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => Read(assembly, n))
            .OrderBy(m => m.Version)];
    }

    private static Migration Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Line endings are normalised so a checkout with CRLF does not look like an edited migration.
        var sql = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var file = Path.GetFileNameWithoutExtension(resource["migrations/".Length..]);
        var separator = file.IndexOf('_', StringComparison.Ordinal);
        var version = int.Parse(file[..separator], System.Globalization.CultureInfo.InvariantCulture);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        return new Migration(version, file[(separator + 1)..], sql, hash);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied migration {Version} {Name}")]
    private partial void LogApplied(int version, string name);
}
