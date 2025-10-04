namespace Marconian.ResearchAgent.Configuration;

public sealed class ResearcherOptions
{
    public int MaxSearchIterations { get; set; } = 3;
    public int MaxContinuationQueries { get; set; } = 2;
    public int RelatedMemoryTake { get; set; } = 3;
}
