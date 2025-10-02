using System.Threading;
using System.Threading.Tasks;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public interface IComputerUseExplorer
{
    Task<ComputerUseExplorationResult> ExploreAsync(string url, string? objective = null, CancellationToken cancellationToken = default);
}
