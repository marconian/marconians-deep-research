using Marconian.ResearchAgent.Models.Memory;

namespace Marconian.ResearchAgent.Services.Cosmos;

public interface ICosmosMemoryService : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertRecordAsync(MemoryRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryRecord>> QueryBySessionAsync(string researchSessionId, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryRecord>> QueryByTypeAsync(string type, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySearchResult>> QuerySimilarAsync(string researchSessionId, IReadOnlyList<float> embedding, int limit, CancellationToken cancellationToken = default);

}
