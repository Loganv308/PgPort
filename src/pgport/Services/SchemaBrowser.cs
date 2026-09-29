using PgPort.Models;
using Npgsql;

namespace PgPort.Services;

/// <summary>Lists schemas, tables/views and their columns for the sidebar's table browser.</summary>
public static class SchemaBrowser
{
    // One round trip for everything. pg_catalog rather than information_schema: it's much faster on
    // databases with many tables, and it still lists tables the role can't read (CanSelect = false),
    // so a missing GRANT shows up in the browser instead of the table silently vanishing.
    // Partitions are hidden; their parent table is listed.
    private const string Sql = """
        SELECT n.nspname,
               c.relname,
               c.relkind::text,
               c.reltuples::bigint,
               has_schema_privilege(n.oid, 'USAGE') AND has_any_column_privilege(c.oid, 'SELECT'),
               a.attname,
               format_type(a.atttypid, a.atttypmod),
               a.attnotnull
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
          AND NOT c.relispartition
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname !~ '^pg_(toast|temp_)'
        ORDER BY n.nspname, c.relname, a.attnum
        """;

    public static Task<List<SchemaInfo>> LoadAsync(
        QueryRunner runner, NpgsqlDataSource dataSource, int timeoutSeconds, CancellationToken ct) =>
        runner.RunAsync(dataSource, Sql, timeoutSeconds, async reader =>
        {
            var schemas = new List<SchemaInfo>();
            List<TableInfo> tables = [];
            List<TableColumn> columns = [];
            string? currentSchema = null, currentTable = null;

            // Rows arrive ordered by schema, then table, so each group is contiguous.
            while (await reader.ReadAsync(ct))
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);

                if (schema != currentSchema)
                {
                    tables = [];
                    schemas.Add(new SchemaInfo(schema, tables));
                    currentSchema = schema;
                    currentTable = null;
                }

                if (table != currentTable)
                {
                    var kind = reader.GetString(2);
                    var tuples = reader.GetInt64(3);
                    // reltuples is only meaningful for tables and materialized views; -1 means "never analyzed".
                    long? estimate = kind is "r" or "p" or "m" && tuples >= 0 ? tuples : null;

                    columns = [];
                    tables.Add(new TableInfo(table, KindName(kind), estimate, reader.GetBoolean(4), columns));
                    currentTable = table;
                }

                if (!reader.IsDBNull(5))
                    columns.Add(new TableColumn(reader.GetString(5), reader.GetString(6), reader.GetBoolean(7)));
            }

            return schemas;
        }, ct);

    private static string KindName(string relkind) => relkind switch
    {
        "v" => "view",
        "m" => "materialized view",
        "f" => "foreign table",
        _ => "table",
    };
}
