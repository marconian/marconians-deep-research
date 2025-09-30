using System.Text.Json.Serialization;

namespace Marconian.ResearchAgent.Models.Files;

public sealed class FileRegistryManifest
{
    [JsonPropertyName("files")]
    public List<FileRegistryEntry> Files { get; init; } = new();
}
