using System.Text.Json;
using Microsoft.Extensions.Options;
using PgPort.Models;
using PgPort.Options;

namespace PgPort.Services;

/// <summary>
/// Saved queries, kept in one JSON file under Storage:DataDirectory. The list is small, so it lives
/// in memory and every change rewrites the file. Writes go to a temp file first so a crash mid-write
/// can't leave a half-written list behind.
/// </summary>
public sealed class SavedQueryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger<SavedQueryStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<SavedQuery>? _items;

    public SavedQueryStore(IOptions<StorageOptions> options, IHostEnvironment env, ILogger<SavedQueryStore> logger)
    {
        var directory = Path.GetFullPath(options.Value.DataDirectory, env.ContentRootPath);
        FilePath = Path.Combine(directory, "saved-queries.json");
        _logger = logger;
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<SavedQuery>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return (await LoadAsync(ct)).OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<SavedQuery> AddAsync(string name, string? database, string sql, CancellationToken ct) =>
        MutateAsync(items =>
        {
            var now = DateTimeOffset.UtcNow;
            var query = new SavedQuery(Guid.NewGuid().ToString("N"), name, database, sql, now, now);
            items.Add(query);
            return query;
        }, ct);

    /// <summary>Returns null if no saved query has that id.</summary>
    public Task<SavedQuery?> UpdateAsync(string id, string name, string? database, string sql, CancellationToken ct) =>
        MutateAsync(items =>
        {
            var index = items.FindIndex(q => q.Id == id);
            if (index < 0) return null;

            var updated = items[index] with { Name = name, Database = database, Sql = sql, UpdatedAt = DateTimeOffset.UtcNow };
            items[index] = updated;
            return updated;
        }, ct);

    public Task<bool> DeleteAsync(string id, CancellationToken ct) =>
        MutateAsync(items => items.RemoveAll(q => q.Id == id) > 0, ct);

    /// <summary>Applies a change to a copy of the list and only keeps it once the file has been written.</summary>
    private async Task<T> MutateAsync<T>(Func<List<SavedQuery>, T> change, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var items = new List<SavedQuery>(await LoadAsync(ct));
            var result = change(items);
            await SaveAsync(items);
            _items = items;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<SavedQuery>> LoadAsync(CancellationToken ct)
    {
        if (_items is not null) return _items;
        if (!File.Exists(FilePath)) return _items = [];

        try
        {
            await using var stream = File.OpenRead(FilePath);
            _items = await JsonSerializer.DeserializeAsync<List<SavedQuery>>(stream, Json, ct) ?? [];
        }
        catch (JsonException ex)
        {
            // Keep the unreadable file for manual recovery instead of overwriting it on the next save.
            var backup = $"{FilePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            File.Move(FilePath, backup);
            _logger.LogError(ex, "Couldn't parse {Path}; moved it to {Backup} and started an empty list", FilePath, backup);
            _items = [];
        }
        return _items;
    }

    private async Task SaveAsync(List<SavedQuery> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        // Not cancellable: a client disconnecting mid-save shouldn't abandon a partial write.
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, items, Json, CancellationToken.None);
        }
        File.Move(temp, FilePath, overwrite: true);
    }
}
