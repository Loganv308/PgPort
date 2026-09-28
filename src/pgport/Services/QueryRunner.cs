using Npgsql;

namespace PgPort.Services;

/// <summary>
/// Runs a single statement inside a READ ONLY transaction with a server-side statement_timeout,
/// then always rolls back. Pair this with a SELECT-only database role (see docs/readonly-role.sql).
/// </summary>
public sealed class QueryRunner
{
    public async Task<T> RunAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        int timeoutSeconds,
        Func<NpgsqlDataReader, Task<T>> readResults,
        CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var timeoutMs = Math.Max(1, timeoutSeconds) * 1000L;
        await using (var setup = new NpgsqlCommand(
            $"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = {timeoutMs}", conn, tx))
        {
            await setup.ExecuteNonQueryAsync(ct);
        }

        T result;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            // Rely on the server-side statement_timeout (plus request cancellation) instead of
            // Npgsql's 30s client-side default, which would cut long exports short.
            cmd.CommandTimeout = 0;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            result = await readResults(reader);
        }

        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Transaction already completed; nothing to roll back.
        }
        return result;
    }

    /// <summary>Safely reads a column; exotic types that Npgsql can't map become a readable placeholder.</summary>
    public static object? ReadValue(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try
        {
            return reader.GetValue(ordinal);
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or OverflowException)
        {
            return $"<unreadable {reader.GetDataTypeName(ordinal)}>";
        }
    }
}
