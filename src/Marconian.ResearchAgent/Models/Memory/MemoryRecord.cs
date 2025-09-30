using System.Text.Json.Serialization;

namespace Marconian.ResearchAgent.Models.Memory;

public sealed class MemoryRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("researchSessionId")]
    public required string ResearchSessionId { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("embedding")]
    public List<float> Embedding { get; init; } = new();

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new();

    [JsonPropertyName("sources")]
    public List<MemorySourceReference> Sources { get; init; } = new();
}

public sealed record MemorySourceReference(
    string SourceId,
    string? Title,
    string? Url,
    string? Snippet);
