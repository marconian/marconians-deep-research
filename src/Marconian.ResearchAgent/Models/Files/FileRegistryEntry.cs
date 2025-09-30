using System.Text.Json.Serialization;

namespace Marconian.ResearchAgent.Models.Files;

public sealed class FileRegistryEntry
{
    [JsonPropertyName("fileId")]
    public string FileId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("relativePath")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("downloadedAtUtc")]
    public DateTimeOffset DownloadedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
