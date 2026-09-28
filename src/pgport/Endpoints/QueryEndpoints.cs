using System.Diagnostics;
using PgPort.Models;
using PgPort.Options;
using PgPort.Services;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PgPort.Endpoints;

public static class QueryEndpoints
{
    public static void MapQueryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/query", RunQueryAsync);
    }

    private static async Task<IResult> RunQueryAsync(
        QueryRequest request,
        DatabaseRegistry registry,
        QueryRunner runner,
        IOptions<QueryOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var opts = options.Value;
        var (query, error) = PreparedQuery.Create(registry, request.Database, request.Sql);
        if (error is not null) return Results.BadRequest(error);

        var pageSize = Math.Clamp(request.PageSize ?? opts.DefaultPageSize, 1, opts.MaxPageSize);
        var page = Math.Max(1, request.Page ?? 1);

        // SELECT / WITH / VALUES / TABLE get wrapped so Postgres does the paging.
        // Anything else (EXPLAIN, SHOW, ...) runs as-is and only the first page is shown.
        string sql;
        int prefixLength;
        if (query!.Wrappable)
        {
            sql = query.WithLimit(pageSize + 1L, (page - 1L) * pageSize);
            prefixLength = PreparedQuery.WrapPrefix.Length;
        }
        else
        {
            page = 1;
            sql = query.Statement;
            prefixLength = 0;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (columns, rows, hasMore) = await runner.RunAsync(query.DataSource, sql, opts.QueryTimeoutSeconds, async reader =>
            {
                var cols = new List<ColumnInfo>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                    cols.Add(new ColumnInfo(reader.GetName(i), reader.GetDataTypeName(i)));

                var list = new List<object?[]>();
                var more = false;
                while (await reader.ReadAsync(ct))
                {
                    if (list.Count == pageSize) { more = true; break; }

                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                        row[i] = ValueFormatter.ToJson(QueryRunner.ReadValue(reader, i), opts.PreviewMaxCellLength);
                    list.Add(row);
                }
                return (cols, list, more);
            }, ct);

            return Results.Ok(new QueryResponse(columns, rows, page, pageSize, hasMore, query.Wrappable, stopwatch.ElapsedMilliseconds));
        }
        catch (PostgresException ex)
        {
            return Results.BadRequest(DbErrors.FromPostgres(ex, query.Statement, prefixLength));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.Empty; // Browser went away (e.g. user pressed Cancel).
        }
        catch (NpgsqlException ex)
        {
            loggerFactory.CreateLogger("Query").LogWarning(ex, "Database error for {Database}", query.Database);
            return Results.Json(DbErrors.FromConnection(ex, query.Database), statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
