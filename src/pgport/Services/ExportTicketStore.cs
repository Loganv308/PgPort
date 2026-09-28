using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace PgPort.Services;

/// <summary>
/// Short-lived, single-use tickets. The browser validates a query with a JSON POST, then follows a
/// plain GET link so the file streams straight into the browser's native download manager
/// (no buffering the whole export in page memory).
/// </summary>
public sealed class ExportTicketStore(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public string Issue(PreparedQuery query)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        cache.Set(Key(token), query, Lifetime);
        return token;
    }

    public PreparedQuery? Redeem(string token)
    {
        if (!cache.TryGetValue(Key(token), out PreparedQuery? query)) return null;
        cache.Remove(Key(token));
        return query;
    }

    private static string Key(string token) => "export:" + token;
}
