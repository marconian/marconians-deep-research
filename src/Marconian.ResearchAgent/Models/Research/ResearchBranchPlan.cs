namespace Marconian.ResearchAgent.Models.Research;

public sealed class ResearchBranchPlan
{
    public string BranchId { get; init; } = Guid.NewGuid().ToString("N");

    public required string Question { get; init; }

    public ResearchBranchStatus Status { get; set; } = ResearchBranchStatus.Pending;

    public List<string> ToolPreferences { get; init; } = new();
}

public enum ResearchBranchStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}
