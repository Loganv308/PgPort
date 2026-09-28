using System.Text;
using System.Text.Json;
using System.Xml;

namespace PgPort.Services;

/// <summary>
/// Streams query rows to an output stream in one export format.
/// Usage: Create -> WriteRowAsync for each row -> CompleteAsync.
/// </summary>
public abstract class ExportWriter : IAsyncDisposable
{
    public static readonly IReadOnlyList<string> Formats = ["csv", "json", "xml"];

    public static bool IsSupported(string format) => Formats.Contains(format.ToLowerInvariant());

    public static string ContentType(string format) => format.ToLowerInvariant() switch
    {
        "csv" => "text/csv; charset=utf-8",
        "json" => "application/json; charset=utf-8",
        "xml" => "application/xml; charset=utf-8",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static ExportWriter Create(string format, Stream output, IReadOnlyList<string> columns) =>
        format.ToLowerInvariant() switch
        {
            "csv" => new CsvExportWriter(output, columns),
            "json" => new JsonExportWriter(output, columns),
            "xml" => new XmlExportWriter(output, columns),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    /// <summary>Writes the header / opening wrapper. Called once before the first row.</summary>
    public abstract Task StartAsync(CancellationToken ct);

    /// <summary>Values are raw Npgsql values (as returned by QueryRunner.ReadValue).</summary>
    public abstract Task WriteRowAsync(object?[] values, CancellationToken ct);

    /// <summary>Writes any closing wrapper and flushes.</summary>
    public abstract Task CompleteAsync(CancellationToken ct);

    public abstract ValueTask DisposeAsync();
}

internal sealed class CsvExportWriter(Stream output, IReadOnlyList<string> columns) : ExportWriter
{
    // UTF-8 with BOM so Excel shows emoji and non-Latin text correctly.
    private readonly StreamWriter _writer = new(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024, leaveOpen: true);
    private readonly string[] _fields = new string[columns.Count];

    public override Task StartAsync(CancellationToken ct) => CsvFormat.WriteRowAsync(_writer, columns);

    public override Task WriteRowAsync(object?[] values, CancellationToken ct)
    {
        for (var i = 0; i < _fields.Length; i++) _fields[i] = ValueFormatter.ToText(values[i]);
        return CsvFormat.WriteRowAsync(_writer, _fields);
    }

    public override Task CompleteAsync(CancellationToken ct) => _writer.FlushAsync(ct);

    public override ValueTask DisposeAsync() => _writer.DisposeAsync();
}

/// <summary>A JSON array of objects: [{"col": value, ...}, ...]. Numbers, booleans and nulls keep their JSON types.</summary>
internal sealed class JsonExportWriter : ExportWriter
{
    private readonly ChunkBuffer _buffer;
    private readonly Utf8JsonWriter _json;
    private readonly IReadOnlyList<string> _columns;

    public JsonExportWriter(Stream output, IReadOnlyList<string> columns)
    {
        _columns = columns;
        _buffer = new ChunkBuffer(output);
        // Writes go to an in-memory buffer (JsonSerializer flushes synchronously, which ASP.NET Core
        // forbids on the response stream); the buffer is copied to the response asynchronously.
        _json = new Utf8JsonWriter(_buffer.Memory, new JsonWriterOptions
        {
            Indented = false,
            // Keep accents and quotes readable instead of \uXXXX escapes (still valid JSON).
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public override Task StartAsync(CancellationToken ct)
    {
        _json.WriteStartArray();
        return Task.CompletedTask;
    }

    public override Task WriteRowAsync(object?[] values, CancellationToken ct)
    {
        _json.WriteStartObject();
        for (var i = 0; i < values.Length; i++)
        {
            _json.WritePropertyName(_columns[i]);
            JsonSerializer.Serialize(_json, ValueFormatter.ToJson(values[i], int.MaxValue));
        }
        _json.WriteEndObject();
        _json.Flush(); // into the memory buffer only
        return _buffer.DrainIfFullAsync(ct);
    }

    public override async Task CompleteAsync(CancellationToken ct)
    {
        _json.WriteEndArray();
        _json.Flush();
        await _buffer.DrainAsync(ct);
    }

    public override ValueTask DisposeAsync() => _json.DisposeAsync();
}

/// <summary>
/// An in-memory staging buffer that is copied to the real output with async writes once it
/// passes 64 KB, so memory stays flat and nothing ever writes synchronously to the response.
/// </summary>
internal sealed class ChunkBuffer(Stream output)
{
    private const int Threshold = 64 * 1024;

    public MemoryStream Memory { get; } = new(Threshold * 2);

    public Task DrainIfFullAsync(CancellationToken ct) =>
        Memory.Length >= Threshold ? DrainAsync(ct) : Task.CompletedTask;

    public async Task DrainAsync(CancellationToken ct)
    {
        if (Memory.Length == 0) return;
        await output.WriteAsync(Memory.GetBuffer().AsMemory(0, (int)Memory.Length), ct);
        Memory.SetLength(0);
        Memory.Position = 0;
    }
}

/// <summary>
/// &lt;rows&gt;&lt;row&gt;&lt;column_name&gt;value&lt;/column_name&gt;...&lt;/row&gt;&lt;/rows&gt;.
/// NULLs are written as &lt;col null="true"/&gt; so they differ from empty strings.
/// </summary>
internal sealed class XmlExportWriter : ExportWriter
{
    private readonly ChunkBuffer _buffer;
    private readonly XmlWriter _xml;
    private readonly string[] _elementNames;

    public XmlExportWriter(Stream output, IReadOnlyList<string> columns)
    {
        // XmlWriter writes to memory (its Dispose/Flush are synchronous); ChunkBuffer copies to the response async.
        _buffer = new ChunkBuffer(output);
        _xml = XmlWriter.Create(_buffer.Memory, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            CloseOutput = false,
        });
        // Column names like "count(*)" or "?column?" aren't valid element names; encode them.
        _elementNames = columns.Select(c => XmlConvert.EncodeLocalName(string.IsNullOrEmpty(c) ? "column" : c)!).ToArray();
    }

    public override Task StartAsync(CancellationToken ct)
    {
        _xml.WriteStartDocument();
        _xml.WriteStartElement("rows");
        return Task.CompletedTask;
    }

    public override Task WriteRowAsync(object?[] values, CancellationToken ct)
    {
        _xml.WriteStartElement("row");
        for (var i = 0; i < values.Length; i++)
        {
            _xml.WriteStartElement(_elementNames[i]);
            if (values[i] is null)
                _xml.WriteAttributeString("null", "true");
            else
                _xml.WriteString(RemoveInvalidXmlChars(ValueFormatter.ToText(values[i])));
            _xml.WriteEndElement();
        }
        _xml.WriteEndElement();
        _xml.Flush(); // into the memory buffer only
        return _buffer.DrainIfFullAsync(ct);
    }

    public override async Task CompleteAsync(CancellationToken ct)
    {
        _xml.WriteEndElement();
        _xml.WriteEndDocument();
        _xml.Flush();
        await _buffer.DrainAsync(ct);
    }

    public override ValueTask DisposeAsync()
    {
        _xml.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// XML 1.0 can't contain most control characters (chat messages sometimes do), which would make
    /// XmlWriter throw mid-export. Drops them while keeping emoji (surrogate pairs) intact.
    /// </summary>
    private static string RemoveInvalidXmlChars(string text)
    {
        var firstBad = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i])) continue;
            if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i])) { i++; continue; }
            firstBad = i;
            break;
        }
        if (firstBad < 0) return text;

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, firstBad);
        for (var i = firstBad; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i])) sb.Append(text[i]);
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i])) { sb.Append(text, i, 2); i++; }
        }
        return sb.ToString();
    }
}