using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;

namespace Marconian.ResearchAgent.Models.Research;

public sealed class ResearchBranchResult
{
    public required string BranchId { get; init; }

    public required ResearchFinding Finding { get; init; }

    public List<ToolExecutionResult> ToolOutputs { get; init; } = new();

    public List<string> Notes { get; init; } = new();
}
