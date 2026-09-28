using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PgPort.Services;

/// <summary>
/// Converts values coming out of Npgsql into (a) JSON-safe values for the preview grid and
/// (b) plain text for CSV. Unknown types fall back to ToString() so a query never fails to
/// render just because a column has an exotic type.
/// </summary>
public static class ValueFormatter
{
    private const long MaxSafeJsonInteger = 9_007_199_254_740_991; // 2^53 - 1
    private const int PreviewMaxBytes = 64;

    public static object? ToJson(object? value, int maxStringLength)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case string s:
                return Truncate(s, maxStringLength);
            case bool or byte or sbyte or short or ushort or int or uint:
                return value;
            case long l:
                return l is <= MaxSafeJsonInteger and >= -MaxSafeJsonInteger ? l : l.ToString(CultureInfo.InvariantCulture);
            case ulong ul:
                return ul <= MaxSafeJsonInteger ? ul : ul.ToString(CultureInfo.InvariantCulture);
            case float f:
                return float.IsFinite(f) ? f : f.ToString(CultureInfo.InvariantCulture);
            case double d:
                return double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture);
            case decimal m:
                // Keep full precision; JS numbers would round.
                return m.ToString(CultureInfo.InvariantCulture);
            case byte[] bytes:
                return ToHex(bytes, PreviewMaxBytes);
            case IDictionary dict:
            {
                var result = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dict)
                    result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = ToJson(entry.Value, maxStringLength);
                return result;
            }
            case IEnumerable seq:
            {
                var list = new List<object?>();
                foreach (var item in seq) list.Add(ToJson(item, maxStringLength));
                return list;
            }
            default:
                return Truncate(ToText(value), maxStringLength);
        }
    }

    /// <summary>Full-fidelity text representation used for exports.</summary>
    public static string ToText(object? value) => value switch
    {
        null or DBNull => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + (dt.Kind == DateTimeKind.Utc ? "Z" : ""),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => ToHex(bytes, int.MaxValue),
        IDictionary or (IEnumerable and not string) => JsonSerializer.Serialize(ToJson(value, int.MaxValue)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private static string ToHex(byte[] bytes, int maxBytes)
    {
        var take = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder(2 + take * 2 + 1);
        sb.Append("\\x").Append(Convert.ToHexString(bytes, 0, take).ToLowerInvariant());
        if (take < bytes.Length) sb.Append('…');
        return sb.ToString();
    }
}
