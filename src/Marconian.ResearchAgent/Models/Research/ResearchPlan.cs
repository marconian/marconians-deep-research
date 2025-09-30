namespace Marconian.ResearchAgent.Models.Research;

public sealed class ResearchPlan
{
    public required string ResearchSessionId { get; init; }

    public required string RootQuestion { get; init; }

    public List<ResearchBranchPlan> Branches { get; init; } = new();

    public List<string> Assumptions { get; init; } = new();
}
