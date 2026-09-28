using PgPort.Endpoints;
using PgPort.Options;
using PgPort.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<QueryOptions>(builder.Configuration.GetSection(QueryOptions.SectionName));
builder.Services.AddSingleton<DatabaseRegistry>();
builder.Services.AddSingleton<QueryRunner>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ExportTicketStore>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    // Drops null *properties* (e.g. unused error fields); null cell values inside rows are kept.
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// Fail fast (with a clear log line) if no databases are configured.
var registry = app.Services.GetRequiredService<DatabaseRegistry>();
if (registry.Names.Count == 0)
{
    app.Logger.LogWarning(
        "No databases configured. Set Databases__<Name>=<connection string> (e.g. Databases__Twitch=Host=...;Database=...;Username=...;Password=...).");
}
else
{
    app.Logger.LogInformation("Configured databases: {Databases}", string.Join(", ", registry.Names));
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapDatabaseEndpoints();
app.MapQueryEndpoints();
app.MapExportEndpoints();

app.Run();
