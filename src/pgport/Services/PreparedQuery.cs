using PgPort.Models;
using Npgsql;

namespace PgPort.Services;

/// <summary>A validated, single SQL statement bound to a configured database.</summary>
public sealed record PreparedQuery(string Database, NpgsqlDataSource DataSource, string Statement, bool Wrappable)
{
    public const string WrapPrefix = "SELECT * FROM (\n";

    /// <summary>Wraps the statement so Postgres does the paging (only valid when <see cref="Wrappable"/>).</summary>
    public string WithLimit(long limit, long offset) =>
        $"{WrapPrefix}{Statement}\n) AS _q LIMIT {limit} OFFSET {offset}";

    public static (PreparedQuery? Query, ApiError? Error) Create(DatabaseRegistry registry, string? database, string? sql)
    {
        if (!registry.TryGet(database, out var dataSource))
            return (null, new ApiError($"Unknown database '{database}'."));

        if (string.IsNullOrWhiteSpace(sql))
            return (null, new ApiError("Enter a query to run."));

        var analysis = SqlText.Analyze(sql);

        if (analysis.StatementCount == 0)
            return (null, new ApiError("Enter a query to run."));

        if (analysis.StatementCount > 1)
            return (null, new ApiError(
                $"Found {analysis.StatementCount} statements. Run one statement at a time.",
                Hint: "Select just the statement you want, or remove the extra semicolon-separated statements."));

        if (SqlText.IsTransactionControl(analysis.LeadingKeyword))
            return (null, new ApiError(
                $"'{analysis.LeadingKeyword!.ToUpperInvariant()}' isn't allowed: queries always run in a read-only transaction."));

        return (new PreparedQuery(database!, dataSource, analysis.Statement, SqlText.IsWrappable(analysis.LeadingKeyword)), null);
    }
}
