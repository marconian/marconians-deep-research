using System.Globalization;
using System.Linq;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Services.Cosmos;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Marconian.ResearchAgent.Utilities;

namespace Marconian.ResearchAgent.Memory;

public sealed class LongTermMemoryManager
{
    private readonly ICosmosMemoryService _cosmosMemoryService;
    private readonly IAzureOpenAiService _openAiService;
    private readonly string _embeddingDeploymentName;
    private readonly ILogger<LongTermMemoryManager> _logger;
    private const int EmbeddingChunkTokenLimit = 6000;
    public LongTermMemoryManager(
        ICosmosMemoryService cosmosMemoryService,
        IAzureOpenAiService openAiService,
        string embeddingDeploymentName,
        ILogger<LongTermMemoryManager>? logger = null)
    {
        _cosmosMemoryService = cosmosMemoryService ?? throw new ArgumentNullException(nameof(cosmosMemoryService));
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _embeddingDeploymentName = string.IsNullOrWhiteSpace(embeddingDeploymentName)
            ? throw new ArgumentException("Embedding deployment name must be provided.", nameof(embeddingDeploymentName))
            : embeddingDeploymentName;
        _logger = logger ?? NullLogger<LongTermMemoryManager>.Instance;
    }

    public async Task<MemoryRecord> StoreFindingAsync(
        string researchSessionId,
        ResearchFinding finding,
        string memoryType = "research_finding",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var metadata = new Dictionary<string, string>
        {
            ["title"] = finding.Title,
            ["confidence"] = finding.Confidence.ToString("F2")
        };

        var sources = finding.Citations
            .Select(citation => new MemorySourceReference(citation.SourceId, citation.Title, citation.Url, citation.Snippet))
            .ToList();

        var records = await StoreChunkedRecordsAsync(
            researchSessionId,
            finding.Content,
            memoryType,
            sources,
            metadata,
            cancellationToken).ConfigureAwait(false);

        var primary = records.First();
        _logger.LogInformation(
            "Stored research finding {FindingId} for session {SessionId} across {ChunkCount} chunk(s).",
            primary.Id,
            researchSessionId,
            records.Count);
        return primary;
    }

    public async Task<MemoryRecord> StoreDocumentAsync(
        string researchSessionId,
        string content,
        string memoryType,
        IEnumerable<SourceCitation> citations,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var sources = citations.Select(c => new MemorySourceReference(c.SourceId, c.Title, c.Url, c.Snippet)).ToList();
        var metadataDictionary = metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(metadata);

        var records = await StoreChunkedRecordsAsync(
            researchSessionId,
            content,
            memoryType,
            sources,
            metadataDictionary,
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Stored document of type {Type} for session {SessionId} across {ChunkCount} chunk(s).",
            memoryType,
            researchSessionId,
            records.Count);

        return records.First();
    }

    public Task<IReadOnlyList<MemoryRecord>> GetRecentMemoriesAsync(string researchSessionId, int limit, CancellationToken cancellationToken = default)
        => _cosmosMemoryService.QueryBySessionAsync(researchSessionId, limit, cancellationToken);

    public async Task<IReadOnlyList<MemorySearchResult>> SearchRelevantAsync(
        string researchSessionId,
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var embedding = await GenerateEmbeddingAsync(query, cancellationToken).ConfigureAwait(false);
        return await _cosmosMemoryService.QuerySimilarAsync(researchSessionId, embedding, limit, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertBranchStateAsync(
        string researchSessionId,
        string branchId,
        string question,
        string status,
        string? summary = null,
        IReadOnlyCollection<string>? relatedToolIds = null,
        CancellationToken cancellationToken = default)
    {
        var record = new MemoryRecord
        {
            Id = $"{researchSessionId}_branch_{branchId}",
            ResearchSessionId = researchSessionId,
            Type = "branch_state",
            Content = summary ?? question,
            Embedding = CreatePlaceholderEmbedding(),
            Metadata = new Dictionary<string, string>
            {
                ["branchId"] = branchId,
                ["question"] = question,
                ["status"] = status,
                ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }
        };

        if (relatedToolIds is { Count: > 0 })
        {
            record.Metadata["toolOutputs"] = string.Join(",", relatedToolIds);
        }

        await UpsertSafelyAsync(record, static (logger, recordId) =>
            logger.LogDebug("Persisted branch state {RecordId}.", recordId),
            static (logger, recordId, ex) =>
            logger.LogWarning(ex, "Failed to persist branch state {RecordId}. Continuing without durable state.", recordId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertSessionStateAsync(
        string researchSessionId,
        string state,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var record = new MemoryRecord
        {
            Id = $"{researchSessionId}_session_state",
            ResearchSessionId = researchSessionId,
            Type = "session_state",
            Content = note ?? state,
            Embedding = CreatePlaceholderEmbedding(),
            Metadata = new Dictionary<string, string>
            {
                ["state"] = state,
                ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }
        };

        await UpsertSafelyAsync(record,
            static (logger, recordId) => logger.LogDebug("Persisted session state {RecordId}.", recordId),
            static (logger, recordId, ex) => logger.LogWarning(ex, "Failed to persist session state {RecordId}.", recordId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertSafelyAsync(
        MemoryRecord record,
        Action<ILogger<LongTermMemoryManager>, string> onSuccess,
        Action<ILogger<LongTermMemoryManager>, string, Exception> onFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            await _cosmosMemoryService.UpsertRecordAsync(record, cancellationToken).ConfigureAwait(false);
            onSuccess(_logger, record.Id);
        }
        catch (Exception ex)
        {
            onFailure(_logger, record.Id, ex);
        }
    }

    private async Task<float[]> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken)
    {
        var vector = await _openAiService.GenerateEmbeddingAsync(
            new OpenAiEmbeddingRequest(_embeddingDeploymentName, content),
            cancellationToken).ConfigureAwait(false);

        return vector.Count == 0
            ? CreatePlaceholderEmbedding()
            : vector.ToArray();
    }

    private async Task<List<MemoryRecord>> StoreChunkedRecordsAsync(
        string researchSessionId,
        string content,
        string memoryType,
        List<MemorySourceReference> sources,
        Dictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var chunks = SplitIntoTokenChunks(content, EmbeddingChunkTokenLimit);
        int chunkCount = chunks.Count;
        string baseId = Guid.NewGuid().ToString("N");
        var records = new List<MemoryRecord>(chunkCount);

        if (chunkCount > 1)
        {
            _logger.LogDebug(
                "Chunking content length {Length} into {ChunkCount} segments for session {SessionId}.",
                content.Length,
                chunkCount,
                researchSessionId);
        }

        for (int index = 0; index < chunkCount; index++)
        {
            string chunk = chunks[index];
            var chunkMetadata = new Dictionary<string, string>(metadata);
            if (chunkCount > 1)
            {
                chunkMetadata["chunkIndex"] = (index + 1).ToString(CultureInfo.InvariantCulture);
                chunkMetadata["chunkCount"] = chunkCount.ToString(CultureInfo.InvariantCulture);
                chunkMetadata["chunkParentId"] = baseId;
                chunkMetadata["chunkCharSpan"] = chunk.Length.ToString(CultureInfo.InvariantCulture);
                chunkMetadata["chunkTokenSpan"] = TokenUtilities.CountTokens(chunk).ToString(CultureInfo.InvariantCulture);
            }

            var record = new MemoryRecord
            {
                Id = chunkCount == 1 ? baseId : $"{baseId}_chunk_{index:D4}",
                ResearchSessionId = researchSessionId,
                Type = memoryType,
                Content = chunk,
                Embedding = await GenerateEmbeddingAsync(chunk, cancellationToken).ConfigureAwait(false),
                Metadata = chunkMetadata,
                Sources = new List<MemorySourceReference>(sources)
            };

            await _cosmosMemoryService.UpsertRecordAsync(record, cancellationToken).ConfigureAwait(false);
            records.Add(record);
        }

        return records;
    }

    private static List<string> SplitIntoTokenChunks(string content, int maxTokenLength)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<string> { string.Empty };
        }

        var tokens = TokenUtilities.Encode(content);
        if (tokens.Count <= maxTokenLength)
        {
            return new List<string> { content };
        }

        var chunks = new List<string>();
        var tokenList = tokens is List<int> list ? list : tokens.ToList();
        int position = 0;
        while (position < tokenList.Count)
        {
            int length = Math.Min(maxTokenLength, tokenList.Count - position);
            var slice = tokenList.GetRange(position, length);
            string chunk = TokenUtilities.Decode(slice).Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }

            position += length;
        }

        return chunks.Count == 0 ? new List<string> { content } : chunks;
    }

    private float[] CreatePlaceholderEmbedding()
    {
        int dimensions = Math.Max(1, _cosmosMemoryService.VectorDimensions);
        return Enumerable.Repeat(0f, dimensions).ToArray();
    }
}
