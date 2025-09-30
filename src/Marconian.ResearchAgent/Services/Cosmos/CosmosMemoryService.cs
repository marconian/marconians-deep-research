using System.Linq;
using Microsoft.Azure.Cosmos;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Utilities;

namespace Marconian.ResearchAgent.Services.Cosmos;

public sealed class CosmosMemoryService : ICosmosMemoryService
{
    private const string DefaultDatabaseId = "MarconianMemory";
    private const string DefaultContainerId = "MemoryRecords";

    private readonly CosmosClient _client;
    private readonly string _databaseId;
    private readonly string _containerId;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    private Container? _container;
    private bool _initialized;

    public CosmosMemoryService(Settings.AppSettings settings, string? databaseId = null, string? containerId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _client = new CosmosClient(settings.CosmosConnectionString);
        _databaseId = string.IsNullOrWhiteSpace(databaseId) ? DefaultDatabaseId : databaseId;
        _containerId = string.IsNullOrWhiteSpace(containerId) ? DefaultContainerId : containerId;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertRecordAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var container = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await container.UpsertItemAsync(record, new PartitionKey(record.ResearchSessionId), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryRecord>> QueryBySessionAsync(string researchSessionId, int limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(researchSessionId))
        {
            return Array.Empty<MemoryRecord>();
        }

        var container = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<MemoryRecord>();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.researchSessionId = @session ORDER BY c.createdAtUtc DESC")
            .WithParameter("@session", researchSessionId);

        using FeedIterator<MemoryRecord> iterator = container.GetItemQueryIterator<MemoryRecord>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(researchSessionId),
            MaxItemCount = Math.Max(1, limit)
        });

        while (iterator.HasMoreResults && results.Count < limit)
        {
            foreach (var record in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(record);
                if (results.Count >= limit)
                {
                    break;
                }
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<MemoryRecord>> QueryByTypeAsync(string type, int limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return Array.Empty<MemoryRecord>();
        }

        var container = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<MemoryRecord>();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type ORDER BY c.createdAtUtc DESC")
            .WithParameter("@type", type);

        using FeedIterator<MemoryRecord> iterator = container.GetItemQueryIterator<MemoryRecord>(
            query,
            requestOptions: new QueryRequestOptions
            {
                MaxItemCount = Math.Max(1, limit)
            });

        while (iterator.HasMoreResults && results.Count < limit)
        {
            foreach (var record in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(record);
                if (results.Count >= limit)
                {
                    break;
                }
            }
        }

        return results;
    }
    public async Task<IReadOnlyList<MemorySearchResult>> QuerySimilarAsync(string researchSessionId, IReadOnlyList<float> embedding, int limit, CancellationToken cancellationToken = default)
    {
        if (embedding is not { Count: > 0 })
        {
            return Array.Empty<MemorySearchResult>();
        }

        var candidates = await QueryBySessionAsync(researchSessionId, Math.Max(limit * 4, 20), cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return Array.Empty<MemorySearchResult>();
        }

        var scored = new List<MemorySearchResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.Embedding is not { Count: > 0 })
            {
                continue;
            }

            double similarity = VectorMath.CosineSimilarity(embedding, candidate.Embedding);
            scored.Add(new MemorySearchResult(candidate, similarity));
        }

        return scored
            .OrderByDescending(result => result.SimilarityScore)
            .Take(limit)
            .ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _initializationLock.Dispose();
        await Task.CompletedTask;
    }

    private async Task<Container> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized && _container is not null)
        {
            return _container;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && _container is not null)
            {
                return _container;
            }

            DatabaseResponse databaseResponse = await _client.CreateDatabaseIfNotExistsAsync(_databaseId, cancellationToken: cancellationToken).ConfigureAwait(false);
            var containerProperties = new ContainerProperties(_containerId, partitionKeyPath: "/researchSessionId");
            ContainerResponse containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken: cancellationToken).ConfigureAwait(false);

            _container = containerResponse.Container;
            _initialized = true;
            return _container;
        }
        finally
        {
            _initializationLock.Release();
        }
    }
}
