using PgPort.Models;
using Npgsql;

namespace PgPort.Services;

public static class DbErrors
{
    /// <summary>
    /// Converts a Postgres error into an API error. <paramref name="prefixLength"/> is the number of
    /// characters we prepended to the user's statement (for paging), so error positions point at
    /// the user's text rather than our wrapper.
    /// </summary>
    public static ApiError FromPostgres(PostgresException ex, string statement, int prefixLength)
    {
        int? line = null, column = null;
        string? near = null;

        var index = ex.Position - 1 - prefixLength; // Position is 1-based; 0 means "not provided"
        if (ex.Position > 0 && index >= 0 && index < statement.Length)
        {
            var before = statement.AsSpan(0, index);
            line = before.Count('\n') + 1;
            column = index - (before.LastIndexOf('\n') + 1) + 1;

            var lineStart = before.LastIndexOf('\n') + 1;
            var lineEnd = statement.IndexOf('\n', index);
            if (lineEnd < 0) lineEnd = statement.Length;
            near = statement[lineStart..lineEnd].TrimEnd('\r');
        }

        var message = ex.SqlState == PostgresErrorCodes.QueryCanceled
            ? "The query was cancelled because it exceeded the time limit."
            : ex.MessageText;

        return new ApiError(message, ex.Detail, ex.Hint, ex.SqlState, line, column, near);
    }

    public static ApiError FromConnection(NpgsqlException ex, string database) =>
        new($"Couldn't reach database '{database}': {ex.Message}",
            Hint: "Check the connection string and that the database is reachable from the container.");
}
