using System.Text;
using PgPort.Models;
using PgPort.Options;
using PgPort.Services;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PgPort.Endpoints;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/export/{format}", CreateExportAsync);
        app.MapGet("/api/export/{format}/{token}", DownloadAsync);
    }

    /// <summary>
    /// Step 1: validate the query (including a cheap LIMIT 0 dry run so syntax errors show up in the
    /// UI instead of as a broken download) and hand back a one-time download URL.
    /// </summary>
    private static async Task<IResult> CreateExportAsync(
        string format,
        ExportRequest request,
        DatabaseRegistry registry,
        QueryRunner runner,
        ExportTicketStore tickets,
        IOptions<QueryOptions> options,
        CancellationToken ct)
    {
        if (!ExportWriter.IsSupported(format))
            return Results.BadRequest(new ApiError($"Unsupported export format '{format}'. Use one of: {string.Join(", ", ExportWriter.Formats)}."));

        var (query, error) = PreparedQuery.Create(registry, request.Database, request.Sql);
        if (error is not null) return Results.BadRequest(error);

        if (query!.Wrappable)
        {
            try
            {
                await runner.RunAsync(query.DataSource, query.WithLimit(0, 0), options.Value.QueryTimeoutSeconds,
                    _ => Task.FromResult(true), ct);
            }
            catch (PostgresException ex)
            {
                return Results.BadRequest(DbErrors.FromPostgres(ex, query.Statement, PreparedQuery.WrapPrefix.Length));
            }
            catch (NpgsqlException ex)
            {
                return Results.Json(DbErrors.FromConnection(ex, query.Database), statusCode: StatusCodes.Status502BadGateway);
            }
        }

        var token = tickets.Issue(query);
        return Results.Ok(new ExportTicket($"/api/export/{format.ToLowerInvariant()}/{token}"));
    }

    /// <summary>Step 2: stream every row straight to the response. Memory use stays flat regardless of size.</summary>
    private static async Task DownloadAsync(
        string format,
        string token,
        HttpContext http,
        QueryRunner runner,
        ExportTicketStore tickets,
        IOptions<QueryOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        format = format.ToLowerInvariant();
        var query = ExportWriter.IsSupported(format) ? tickets.Redeem(token) : null;
        if (query is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsync("This download link has expired. Run the export again.", ct);
            return;
        }

        var logger = loggerFactory.CreateLogger("Export");
        var fileName = $"{Slug(query.Database)}-{DateTime.Now:yyyyMMdd-HHmmss}.{format}";
        long rowCount = 0;

        try
        {
            await runner.RunAsync(query.DataSource, query.Statement, options.Value.ExportTimeoutSeconds, async reader =>
            {
                http.Response.ContentType = ExportWriter.ContentType(format);
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                http.Response.Headers.CacheControl = "no-store";

                var columns = new string[reader.FieldCount];
                for (var i = 0; i < columns.Length; i++) columns[i] = reader.GetName(i);

                await using var writer = ExportWriter.Create(format, http.Response.Body, columns);
                await writer.StartAsync(ct);

                var values = new object?[columns.Length];
                while (await reader.ReadAsync(ct))
                {
                    for (var i = 0; i < values.Length; i++)
                        values[i] = QueryRunner.ReadValue(reader, i);
                    await writer.WriteRowAsync(values, ct);
                    rowCount++;
                }

                await writer.CompleteAsync(ct);
                return true;
            }, ct);

            logger.LogInformation("Exported {Rows} rows from {Database} to {File}", rowCount, query.Database, fileName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Export of {Database} cancelled by client after {Rows} rows", query.Database, rowCount);
        }
        catch (NpgsqlException ex)
        {
            logger.LogWarning(ex, "Export of {Database} failed after {Rows} rows", query.Database, rowCount);
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await http.Response.WriteAsync($"Export failed: {ex.Message}", CancellationToken.None);
            }
            else
            {
                // Headers already sent: abort so the browser marks the download as failed
                // instead of saving a silently truncated file.
                http.Abort();
            }
        }
    }

    private static string Slug(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).ToLowerInvariant();
    }
}