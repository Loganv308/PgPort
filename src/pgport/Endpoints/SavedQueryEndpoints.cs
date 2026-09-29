using PgPort.Models;
using PgPort.Services;

namespace PgPort.Endpoints;

public static class SavedQueryEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxSqlLength = 200_000;

    public static void MapSavedQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/saved-queries");
        group.MapGet("", async (SavedQueryStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));
        group.MapPost("", CreateAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
    }

    private static Task<IResult> CreateAsync(SavedQueryRequest request, SavedQueryStore store, ILoggerFactory loggerFactory, CancellationToken ct) =>
        Guard(store, loggerFactory, async () =>
        {
            if (Validate(request) is { } error) return Results.BadRequest(error);

            var saved = await store.AddAsync(request.Name!.Trim(), Blank(request.Database), request.Sql!, ct);
            return Results.Created($"/api/saved-queries/{saved.Id}", saved);
        });

    private static Task<IResult> UpdateAsync(string id, SavedQueryRequest request, SavedQueryStore store, ILoggerFactory loggerFactory, CancellationToken ct) =>
        Guard(store, loggerFactory, async () =>
        {
            if (Validate(request) is { } error) return Results.BadRequest(error);

            var saved = await store.UpdateAsync(id, request.Name!.Trim(), Blank(request.Database), request.Sql!, ct);
            return saved is null ? NotFound() : Results.Ok(saved);
        });

    private static Task<IResult> DeleteAsync(string id, SavedQueryStore store, ILoggerFactory loggerFactory, CancellationToken ct) =>
        Guard(store, loggerFactory, async () =>
            await store.DeleteAsync(id, ct) ? Results.NoContent() : NotFound());

    private static ApiError? Validate(SavedQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return new ApiError("Give the query a name.");
        if (request.Name.Trim().Length > MaxNameLength)
            return new ApiError($"Names can be at most {MaxNameLength} characters.");
        if (string.IsNullOrWhiteSpace(request.Sql))
            return new ApiError("There's no SQL to save.");
        if (request.Sql.Length > MaxSqlLength)
            return new ApiError($"Saved queries can be at most {MaxSqlLength:N0} characters.");
        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IResult NotFound() =>
        Results.NotFound(new ApiError("That saved query no longer exists.", Hint: "It may have been deleted from another tab. Use Save as new instead."));

    /// <summary>Turns file-system failures (usually an unwritable data directory) into a readable API error.</summary>
    private static async Task<IResult> Guard(SavedQueryStore store, ILoggerFactory loggerFactory, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            loggerFactory.CreateLogger("SavedQueries").LogError(ex, "Couldn't write {Path}", store.FilePath);
            return Results.Json(
                new ApiError($"Couldn't write the saved queries file ({store.FilePath}): {ex.Message}",
                    Hint: "Make sure the data directory is writable. In Docker, mount a volume at /app/data."),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
