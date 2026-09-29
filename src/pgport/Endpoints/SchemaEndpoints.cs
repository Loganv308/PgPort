using PgPort.Models;
using PgPort.Options;
using PgPort.Services;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PgPort.Endpoints;

public static class SchemaEndpoints
{
    public static void MapSchemaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/schema", GetSchemaAsync);
    }

    private static async Task<IResult> GetSchemaAsync(
        string? database,
        DatabaseRegistry registry,
        QueryRunner runner,
        IOptions<QueryOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!registry.TryGet(database, out var dataSource))
            return Results.BadRequest(new ApiError($"Unknown database '{database}'."));

        try
        {
            return Results.Ok(await SchemaBrowser.LoadAsync(runner, dataSource, options.Value.QueryTimeoutSeconds, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (PostgresException ex)
        {
            return Results.BadRequest(new ApiError(ex.MessageText, ex.Detail, ex.Hint, ex.SqlState));
        }
        catch (NpgsqlException ex)
        {
            loggerFactory.CreateLogger("Schema").LogWarning(ex, "Database error for {Database}", database);
            return Results.Json(DbErrors.FromConnection(ex, database!), statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
