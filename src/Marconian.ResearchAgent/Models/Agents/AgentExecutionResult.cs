using System.Collections.Generic;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;

namespace Marconian.ResearchAgent.Models.Agents;

public sealed class AgentExecutionResult
{
    public bool Success { get; init; }

    public string Summary { get; init; } = string.Empty;

    public List<ResearchFinding> Findings { get; init; } = new();

    public List<ToolExecutionResult> ToolOutputs { get; init; } = new();

    public List<string> Errors { get; init; } = new();

    public Dictionary<string, string> Metadata { get; init; } = new();

}
