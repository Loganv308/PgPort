using PgPort.Services;

namespace PgPort.Endpoints;

public static class DatabaseEndpoints
{
    public static void MapDatabaseEndpoints(this IEndpointRouteBuilder app)
    {
        // Names only: connection strings never leave the server.
        app.MapGet("/api/databases", (DatabaseRegistry registry) => Results.Ok(registry.Names));
    }
}
