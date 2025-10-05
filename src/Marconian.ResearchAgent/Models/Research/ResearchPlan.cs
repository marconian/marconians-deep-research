namespace Marconian.ResearchAgent.Models.Research;

public sealed class ResearchPlan
{
    public required string ResearchSessionId { get; init; }

    public required string RootQuestion { get; init; }

    public string? Summary { get; init; }

    public List<ResearchBranchPlan> Branches { get; init; } = new();

    public List<string> PlanSteps { get; init; } = new();

    public List<string> KeyQuestions { get; init; } = new();

    public List<string> Assumptions { get; init; } = new();

    public string? Notes { get; init; }

    public bool PlannerContextApplied { get; init; }
}
