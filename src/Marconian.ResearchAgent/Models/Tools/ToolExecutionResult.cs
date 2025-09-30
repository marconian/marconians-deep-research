using Marconian.ResearchAgent.Models.Reporting;

namespace Marconian.ResearchAgent.Models.Tools;

public sealed class ToolExecutionResult
{
    public required string ToolName { get; init; }

    public bool Success { get; init; }

    public string Output { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new();

    public List<SourceCitation> Citations { get; init; } = new();

    public string? ErrorMessage { get; init; }
}
