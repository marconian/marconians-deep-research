using System.Text.Json.Serialization;

namespace Marconian.ResearchAgent.Models.Memory;

public sealed class ShortTermMemoryEntry
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("isSummary")]
    public bool IsSummary { get; init; }
}
