using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Services.Cosmos;

public sealed class CosmosMemoryService : ICosmosMemoryService
{
    private const string DefaultDatabaseId = "MarconianMemory";
    private const string DefaultContainerId = "MemoryRecords";
    private const int DefaultEmbeddingDimensions = 3072;

    private readonly CosmosClient _client;
    private readonly string _databaseId;
    private readonly string _containerId;
    private readonly ILogger<CosmosMemoryService> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    private Container? _container;
    private bool _initialized;
    private int _vectorDimensions = DefaultEmbeddingDimensions;

    public CosmosMemoryService(Settings.AppSettings settings, string? databaseId = null, string? containerId = null, ILogger<CosmosMemoryService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? NullLogger<CosmosMemoryService>.Instance;

        var clientOptions = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            EnableContentResponseOnWrite = false
        };

        _client = new CosmosClient(settings.CosmosConnectionString, clientOptions);
        _databaseId = string.IsNullOrWhiteSpace(databaseId) ? DefaultDatabaseId : databaseId;
        _containerId = string.IsNullOrWhiteSpace(containerId) ? DefaultContainerId : containerId;
        _logger.LogInformation(
            "CosmosMemoryService configured for database '{Database}' and container '{Container}' in gateway mode.",
            _databaseId,
            _containerId);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
    }

    public int VectorDimensions => _vectorDimensions;

    public async Task UpsertRecordAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var container = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Dictionary<string, object?> document = BuildDocument(record);
            _logger.LogDebug(
                "Upserting memory record {RecordId} (type={Type}) for session {SessionId} into container {ContainerId}.",
                record.Id,
                record.Type,
                record.ResearchSessionId,
                container.Id);

            await container.UpsertItemAsync(
                document,
                new PartitionKey(record.ResearchSessionId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex)
        {
            string payload = System.Text.Json.JsonSerializer.Serialize(BuildDocument(record));
            string snippet = payload.Length > 512 ? payload[..512] + "..." : payload;
            _logger.LogWarning(
                ex,
                "Failed to upsert Cosmos record {RecordId} into container {ContainerId}. EmbeddingLength={EmbeddingLength}, ExpectedDimensions={ExpectedDimensions}, Payload: {PayloadSnippet}, Response: {ResponseBody}",
                record.Id,
                container.Id,
                record.Embedding?.Length ?? 0,
                _vectorDimensions,
                snippet,
                ex.ResponseBody);
            throw;
        }
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

        var container = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<MemorySearchResult>();

        var query = new QueryDefinition(
            "SELECT TOP @limit c, VectorDistance(c.embedding, @embedding) AS distance " +
            "FROM c WHERE c.researchSessionId = @session ORDER BY distance")
            .WithParameter("@limit", Math.Max(1, limit))
            .WithParameter("@embedding", embedding)
            .WithParameter("@session", researchSessionId);

        try
        {
            using FeedIterator<CosmosVectorSearchResult> iterator = container.GetItemQueryIterator<CosmosVectorSearchResult>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(researchSessionId),
                    MaxItemCount = Math.Max(1, limit)
                });

            while (iterator.HasMoreResults && results.Count < limit)
            {
                FeedResponse<CosmosVectorSearchResult> response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var item in response)
                {
                    if (item.Record is null)
                    {
                        continue;
                    }

                    double similarity = 1d - item.Distance;
                    similarity = Math.Clamp(similarity, -1d, 1d);
                    results.Add(new MemorySearchResult(item.Record, similarity));

                    if (results.Count >= limit)
                    {
                        break;
                    }
                }
            }

            _logger.LogDebug("Vector similarity query returned {Count} results for session {SessionId}.", results.Count, researchSessionId);
            return results;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Vector similarity query failed; falling back to in-memory scoring.");
            return await FallbackSimilarityAsync(researchSessionId, embedding, limit, cancellationToken).ConfigureAwait(false);
        }
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
            ContainerProperties containerProperties = BuildContainerProperties();
            ContainerResponse containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken: cancellationToken).ConfigureAwait(false);

            UpdateVectorDimensions(containerResponse.Resource);
            _container = containerResponse.Container;
            _initialized = true;
            _logger.LogInformation(
                "Cosmos container '{Container}' initialized with partition key {PartitionKey} and vector dimensions {Dimensions}.",
                _containerId,
                containerResponse.Resource.PartitionKeyPath,
                _vectorDimensions);
            return _container;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private ContainerProperties BuildContainerProperties()
    {
        var properties = new ContainerProperties(_containerId, "/researchSessionId")
        {
            IndexingPolicy = new IndexingPolicy
            {
                Automatic = true,
                IndexingMode = IndexingMode.Consistent
            }
        };

        properties.IndexingPolicy.IncludedPaths.Clear();
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        properties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = "/embedding",
            Type = VectorIndexType.DiskANN
        });

        properties.VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Embedding>
        {
            new Embedding
            {
                Path = "/embedding",
                Dimensions = DefaultEmbeddingDimensions,
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine
            }
        });

        return properties;
    }

    private void UpdateVectorDimensions(ContainerProperties properties)
    {
        var embedding = properties.VectorEmbeddingPolicy?
            .Embeddings
            .FirstOrDefault(static path => string.Equals(path.Path, "/embedding", StringComparison.OrdinalIgnoreCase));

        if (embedding is null || embedding.Dimensions <= 0)
        {
            _logger.LogWarning(
                "Container '{Container}' does not define a vector embedding policy. Using default dimension {Dimensions}.",
                _containerId,
                _vectorDimensions);
            return;
        }

        _vectorDimensions = embedding.Dimensions;
        _logger.LogDebug(
            "Container '{Container}' embedding path {Path} configured for {Dimensions} dimensions.",
            _containerId,
            embedding.Path,
            _vectorDimensions);
    }

    private Dictionary<string, object?> BuildDocument(MemoryRecord record)
    {
        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = record.Id,
            ["researchSessionId"] = record.ResearchSessionId,
            ["type"] = record.Type,
            ["content"] = record.Content,
            ["createdAtUtc"] = record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["metadata"] = new Dictionary<string, string>(record.Metadata, StringComparer.Ordinal),
            ["sources"] = record.Sources.Count == 0
                ? Array.Empty<Dictionary<string, string?>>()
                : record.Sources
                    .Select(source => new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["sourceId"] = source.SourceId,
                        ["title"] = source.Title,
                        ["url"] = source.Url,
                        ["snippet"] = source.Snippet
                    })
                    .ToArray()
        };

        if (record.Embedding is { Length: > 0 } embedding)
        {
            document["embedding"] = NormalizeEmbedding(embedding);
        }

        return document;
    }

    private float[] NormalizeEmbedding(IReadOnlyList<float> embedding)
    {
        int expected = Math.Max(1, _vectorDimensions);
        if (embedding.Count == expected && embedding is float[] floatArray)
        {
            return floatArray;
        }

        var buffer = new float[expected];
        int toCopy = Math.Min(expected, embedding.Count);
        for (int index = 0; index < toCopy; index++)
        {
            buffer[index] = embedding[index];
        }

        if (embedding.Count != expected)
        {
            _logger.LogWarning(
                "Embedding length mismatch for document. Actual={Actual}, Expected={Expected}. Vector will be padded or truncated.",
                embedding.Count,
                expected);
        }

        return buffer;
    }

    private async Task<IReadOnlyList<MemorySearchResult>> FallbackSimilarityAsync(
        string researchSessionId,
        IReadOnlyList<float> embedding,
        int limit,
        CancellationToken cancellationToken)
    {
        var container = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.researchSessionId = @session")
            .WithParameter("@session", researchSessionId);

        var candidates = new List<MemoryRecord>();

        using FeedIterator<MemoryRecord> iterator = container.GetItemQueryIterator<MemoryRecord>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(researchSessionId),
                MaxItemCount = Math.Max(1, limit * 4)
            });

        while (iterator.HasMoreResults)
        {
            foreach (var record in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(record);
            }
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<MemorySearchResult>();
        }

        var scored = new List<MemorySearchResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.Embedding is not { Length: > 0 } vector)
            {
                continue;
            }

            double similarity = VectorMath.CosineSimilarity(embedding, vector);
            scored.Add(new MemorySearchResult(candidate, similarity));
        }

        var ordered = scored
            .OrderByDescending(result => result.SimilarityScore)
            .Take(limit)
            .ToList();

        _logger.LogDebug("Fallback similarity search produced {Count} results for session {SessionId}.", ordered.Count, researchSessionId);
        return ordered;
    }

    private sealed class CosmosVectorSearchResult
    {
        [JsonPropertyName("c")]
        public MemoryRecord? Record { get; init; }

        [JsonPropertyName("distance")]
        public double Distance { get; init; }
    }
}
