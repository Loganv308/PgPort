namespace PgPort.Services;

/// <summary>RFC 4180 CSV field encoding.</summary>
public static class CsvFormat
{
    private static readonly char[] NeedsQuoting = [',', '"', '\r', '\n'];

    public static string Escape(string field)
    {
        var mustQuote = field.IndexOfAny(NeedsQuoting) >= 0
                        || (field.Length > 0 && (char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1])));

        return mustQuote ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }

    public static async Task WriteRowAsync(TextWriter writer, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) await writer.WriteAsync(',');
            await writer.WriteAsync(Escape(fields[i]));
        }
        await writer.WriteAsync("\r\n");
    }
}
