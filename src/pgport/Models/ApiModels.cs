namespace PgPort.Models;

public sealed record QueryRequest(string? Database, string? Sql, int? Page, int? PageSize);

public sealed record ExportRequest(string? Database, string? Sql);

public sealed record ColumnInfo(string Name, string Type);

public sealed record QueryResponse(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<object?[]> Rows,
    int Page,
    int PageSize,
    bool HasMore,
    bool Paged,
    long ElapsedMs);

public sealed record ExportTicket(string Url);

public sealed record ApiError(
    string Error,
    string? Detail = null,
    string? Hint = null,
    string? SqlState = null,
    int? Line = null,
    int? Column = null,
    string? Near = null);
