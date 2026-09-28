namespace PgPort.Options;

public sealed class QueryOptions
{
    public const string SectionName = "Query";

    /// <summary>Server-side statement_timeout for interactive (preview) queries.</summary>
    public int QueryTimeoutSeconds { get; set; } = 60;

    /// <summary>Server-side statement_timeout for exports. Includes time spent streaming rows to the browser.</summary>
    public int ExportTimeoutSeconds { get; set; } = 600;

    public int DefaultPageSize { get; set; } = 100;

    public int MaxPageSize { get; set; } = 1000;

    /// <summary>Strings longer than this are truncated in the preview grid (exports are never truncated).</summary>
    public int PreviewMaxCellLength { get; set; } = 2000;
}
