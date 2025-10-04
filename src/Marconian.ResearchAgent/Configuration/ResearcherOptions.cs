namespace Marconian.ResearchAgent.Configuration;

public sealed class ResearcherOptions
{
    public int MaxSearchIterations { get; set; } = 3;
    public int MaxContinuationQueries { get; set; } = 2;
    public int RelatedMemoryTake { get; set; } = 3;
    public ResearcherParallelismOptions Parallelism { get; set; } = new();
}

public sealed class ResearcherParallelismOptions
{
    public int NavigatorDegreeOfParallelism { get; set; } = 1;
    public int ScraperDegreeOfParallelism { get; set; } = 3;
    public int FileReaderDegreeOfParallelism { get; set; } = 2;
}
