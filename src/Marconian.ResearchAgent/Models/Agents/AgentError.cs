namespace Marconian.ResearchAgent.Models.Agents;

public sealed class AgentError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public Dictionary<string, string> Details { get; init; } = new();
}
