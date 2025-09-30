using Marconian.ResearchAgent.Models.Tools;

namespace Marconian.ResearchAgent.Tools;

public interface ITool
{
    string Name { get; }

    string Description { get; }

    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default);
}
