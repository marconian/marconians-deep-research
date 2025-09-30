using Marconian.ResearchAgent.Models.Agents;

namespace Marconian.ResearchAgent.Agents;

public interface IAgent
{
    Task<AgentExecutionResult> ExecuteTaskAsync(AgentTask task, CancellationToken cancellationToken = default);
}
