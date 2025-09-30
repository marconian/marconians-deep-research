namespace Marconian.ResearchAgent.Models.Tools;

public sealed class ToolExecutionContext
{
    public required string ResearchSessionId { get; init; }

    public required string AgentId { get; init; }

    public required string Instruction { get; init; }

    public Dictionary<string, string> Parameters { get; init; } = new();
}
