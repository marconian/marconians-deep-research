namespace Marconian.ResearchAgent.Models.Agents;

public sealed class AgentTask
{
    public required string TaskId { get; init; }

    public required string ResearchSessionId { get; init; }

    public required string Objective { get; init; }

    public Dictionary<string, string> Parameters { get; init; } = new();

    public List<string> ContextHints { get; init; } = new();
}
