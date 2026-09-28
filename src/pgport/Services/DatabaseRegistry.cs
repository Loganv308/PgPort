using Npgsql;

namespace PgPort.Services;

/// <summary>
/// Holds one pooled NpgsqlDataSource per configured database.
/// Configure with the "Databases" section, e.g. env var Databases__Twitch=Host=...;Database=...
/// </summary>
public sealed class DatabaseRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, NpgsqlDataSource> _sources = new(StringComparer.OrdinalIgnoreCase);

    public DatabaseRegistry(IConfiguration configuration)
    {
        foreach (var child in configuration.GetSection("Databases").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child.Value)) continue;

            var csb = new NpgsqlConnectionStringBuilder(child.Value)
            {
                ApplicationName = "data-extractor",
            };
            _sources[child.Key] = NpgsqlDataSource.Create(csb.ConnectionString);
        }

        Names = _sources.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> Names { get; }

    public bool TryGet(string? name, out NpgsqlDataSource dataSource)
    {
        dataSource = null!;
        return name is not null && _sources.TryGetValue(name, out dataSource!);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values)
            await source.DisposeAsync();
    }
}
