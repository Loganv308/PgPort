namespace PgPort.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Where saved queries are kept. Relative paths resolve against the content root
    /// (/app in the container, so the default is /app/data).
    /// </summary>
    public string DataDirectory { get; set; } = "data";
}
