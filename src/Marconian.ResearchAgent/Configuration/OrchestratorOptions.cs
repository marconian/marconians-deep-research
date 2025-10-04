namespace Marconian.ResearchAgent.Configuration;

public sealed class OrchestratorOptions
{
    public int MaxResearchPasses { get; set; } = 3;
    public int MaxFollowupQuestions { get; set; } = 3;
    public int MaxSectionEvidenceCharacters { get; set; } = 2400;
    public int MaxReportRevisionPasses { get; set; } = 2;
}
