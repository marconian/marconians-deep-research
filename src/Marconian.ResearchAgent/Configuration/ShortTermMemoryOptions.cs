namespace Marconian.ResearchAgent.Configuration;

public sealed class ShortTermMemoryOptions
{
    public int MaxEntries { get; set; } = 40;
    public int SummaryBatchSize { get; set; } = 6;
    public double CacheTtlHours { get; set; } = 6;
}
