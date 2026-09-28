# PgPort

A self-hosted, browser-based SQL console for PostgreSQL. You run a query, page through the results and download them as **CSV, JSON or XML**. It's built for querying the Twitch analytics bot's database without SSHing in.

**Stack:** ASP.NET Core 8 (minimal APIs), Npgsql, and plain HTML/JS with no frontend build step. It ships as a single container.

## Features

- Pick from one or more configured databases. Connection strings stay on the server.
- SQL editor: <kbd>Ctrl</kbd>/<kbd>⌘</kbd>+<kbd>Enter</kbd> runs the query. If text is highlighted, only the highlighted part runs.
- Paged results with column types, NULL highlighting and row numbers. Paging happens in Postgres, so a million-row table doesn't load all at once.
- Error messages show the Postgres error with its line, column and a caret under the problem spot.
- **Export to CSV, JSON or XML.** Pick a format from the *Download Format* dropdown and click **Download**. Exports stream straight to disk, so memory stays flat for any size. The query is dry-run first, so SQL errors show up in the UI instead of as a broken download.
- Cancel button. Closing the tab also cancels the query on the server.

## Export formats

Every format exports **all** rows the query returns. The *Rows per page* setting only affects the on-screen preview.

| Format | Shape | Notes |
|---|---|---|
| CSV | Header row + one line per row (RFC 4180) | UTF‑8 with BOM so emoji and accents show correctly in Excel |
| JSON | `[{"column": value, ...}, ...]` | Numbers, booleans and `null` keep their JSON types. Integers above 2^53 and `numeric` values are strings to keep full precision. |
| XML | `<rows><row><column>value</column>...</row></rows>` | NULL is `<column null="true"/>`, so it's distinct from an empty string. Invalid element names are encoded (`count(*)` → `count_x0028__x002A__x0029_`), so give computed columns an `AS` alias. Control characters that XML 1.0 forbids are removed. |

**Large exports:** the export time limit (`ExportTimeoutSeconds`, default 10 min) includes download time. For very large tables, raise it (e.g. `Query__ExportTimeoutSeconds=7200`) or filter with a `WHERE` clause. Excel opens at most 1,048,576 rows, so export bigger results in slices. For a one-off full-table dump, `psql -c "\copy <table> TO 'file.csv' CSV HEADER"` is faster.

## Safety model

1. **A read-only role (the real guarantee).** Create it with `docs/readonly-role.sql` and put that user in the connection string.
2. Every query runs in a `READ ONLY` transaction that is always rolled back.
3. Only one statement runs per request, and transaction-control statements (`COMMIT`, `BEGIN`, …) are rejected, so a query can't break out of that transaction.
4. A server-side `statement_timeout` applies: 60 s for previews and 10 min for exports.

There's **no login** yet. Put it behind NPMplus with an access list or forward auth, or keep it LAN-only. Authentication is planned for V4.

## Run it with Docker

```bash
cp .env.example .env        # then edit the connection string(s)
docker compose up -d --build
```

Open <http://localhost:8085>. After changing any code (including `wwwroot/` files, which are baked into the image), rebuild with `docker compose up -d --build` and hard-refresh the browser (<kbd>Ctrl</kbd>+<kbd>F5</kbd>). After changing only `.env`, run `docker compose up -d --force-recreate`.

Databases are configured through environment variables. Each variable is named `Databases__<Name>`. `<Name>` is **only the label** shown in the database picker; `Database=` in the connection string decides which database you connect to:

```
Databases__<Db-name>=Host=10.10.0.212;Port=5432;Database=<Db-name>=;Username=pgport_ro;Password=...
```

**Connecting to the bot's Postgres:**
- **Postgres in another compose project:** uncomment the `networks` block in `docker-compose.yml` and use its service name as `Host`.
- **Postgres on the Docker host itself:** use `Host=host.docker.internal` and add `extra_hosts: ["host.docker.internal:host-gateway"]` to the service.

**Behind NPMplus:** proxy `pgport.domain` to `pgport:8080` (shared network) or to `<host>:8085`.

## Run it locally (Windows / VS Code)

```bash
cd src/pgport
# edit appsettings.Development.json with a local connection string, then:
dotnet run
```

`dotnet build` in the same folder is a quick way to catch compile errors before building the Docker image.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Queries return **0 rows** but the table has data | Row-level security (RLS) is enabled and hides rows from `pgport_ro`. Check with `SELECT relname, relrowsecurity FROM pg_class WHERE relkind = 'r';`. Fix (as superuser): `ALTER ROLE pgport_ro BYPASSRLS;`, or add a policy per table: `CREATE POLICY pgport_read ON <table> FOR SELECT TO pgport_ro USING (true);` |
| `permission denied for table …` | The read-only role is missing grants. Run `docs/readonly-role.sql` while connected to the right database (`psql -d <database>`). Materialized views need their own `GRANT SELECT`. |
| `relation "public" does not exist` | `public` is a schema, not a table. Query `public.<table>`. List tables with `SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema');` |
| Not sure which database you're on | `SELECT current_database(), current_user, inet_server_addr();` |
| Download shows **"Failed – network error"** | The server hit an error after the download started. Run `docker compose logs pgport` right after to see the exception. Common causes: the export time limit was reached (raise `Query__ExportTimeoutSeconds`) or the database connection dropped. |
| Export returns 400 | The red error box shows the reason: a SQL error, more than one statement, or an unsupported format (only `csv`, `json`, `xml`). |

## API

| Method | Path | Body | Notes |
|---|---|---|---|
| GET | `/api/health` | | `{ "status": "ok" }` |
| GET | `/api/databases` | | Configured database names |
| POST | `/api/query` | `{ database, sql, page, pageSize }` | Returns columns, rows, `hasMore` and timing |
| POST | `/api/export/{format}` | `{ database, sql }` | `format` is `csv`, `json` or `xml` (case-insensitive). Validates the query and returns a one-time `{ url }` that is valid for 2 minutes |
| GET | `/api/export/{format}/{token}` | | Streams the file |

`SELECT`, `WITH`, `VALUES` and `TABLE` statements are paged by wrapping them as `SELECT * FROM (<your query>) LIMIT … OFFSET …`. Other read-only statements (`EXPLAIN`, `SHOW`) run as written and show only the first page.

## Configuration (`Query__*`)

| Setting | Default | |
|---|---|---|
| `QueryTimeoutSeconds` | 60 | Preview query time limit |
| `ExportTimeoutSeconds` | 600 | Export time limit, including download time. Use a large number rather than `0`; `0` currently becomes a 1-second limit. |
| `DefaultPageSize` | 100 | |
| `MaxPageSize` | 1000 | |
| `PreviewMaxCellLength` | 2000 | Long text is cut off in the grid only. Exports are never cut off. |

## Project layout

```
src/pgport/
  Program.cs                  service wiring + endpoint registration
  Endpoints/                  /api/databases, /api/query, /api/export/{format}
  Services/
    DatabaseRegistry.cs       one pooled NpgsqlDataSource per configured DB
    QueryRunner.cs            read-only transaction + timeout + rollback
    SqlText.cs                statement splitter (quotes, comments, $$ bodies)
    PreparedQuery.cs          validation + LIMIT/OFFSET wrapping
    ValueFormatter.cs         Postgres values -> JSON (grid) / text (exports)
    ExportWriters.cs          CSV / JSON / XML writers (ExportWriter.Create picks one)
    CsvFormat.cs              RFC 4180 escaping
    ExportTicketStore.cs      one-time download tokens
  wwwroot/                    index.html, app.js, app.css
docs/readonly-role.sql
```

### How the UI connects to the API

Controls in `index.html` have an `id`. `app.js` looks them up in the `els` object and attaches handlers in `init()`, and those handlers call the API with `fetch`. Each URL is mapped to a C# method with `app.MapGet`/`app.MapPost` in `Endpoints/`.

| Control | `app.js` | Endpoint |
|---|---|---|
| Run / Ctrl+Enter / Prev / Next / Rows per page | `runFromEditor()` / `goToPage()` → `execute()` | `POST /api/query` → `QueryEndpoints.RunQueryAsync` |
| Database dropdown | `loadDatabases()` | `GET /api/databases` |
| Download Format + Download | `downloadData(els.downloadFormat.value)` | `POST /api/export/{format}` → `GET /api/export/{format}/{token}` in `ExportEndpoints.cs` |

Pass a function to `addEventListener`, for example `() => downloadData(els.downloadFormat.value)`. Passing `downloadData(...)` directly calls it once at page load instead of on click.

### Adding an export format

Add a class deriving from `ExportWriter` in `ExportWriters.cs`, and add the format to `Formats`, `ContentType()` and `Create()`. Then add an `<option>` to the *Download Format* dropdown. The endpoints don't need to change. Writers must never write synchronously to the response stream, because ASP.NET Core rejects it mid-download. Write into `ChunkBuffer` and let it copy to the response asynchronously, as the JSON and XML writers do.

## Roadmap

- **V2:** ~~JSON export~~ ✅, ~~XML export~~ ✅, XLSX export, saved queries, query history, schema/table browser
- **V3:** visual query builder, parameters, charts, Twitch dashboards
- **V4:** authentication, roles, audit log