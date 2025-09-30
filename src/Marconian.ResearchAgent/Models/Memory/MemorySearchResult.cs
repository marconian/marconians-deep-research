namespace Marconian.ResearchAgent.Models.Memory;

public sealed record MemorySearchResult(
    MemoryRecord Record,
    double SimilarityScore);
