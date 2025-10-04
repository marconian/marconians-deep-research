using System.Globalization;
using System.Linq;
using System.Text;
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

    public async Task<IReadOnlyList<ResearchFinding>> RestoreFindingsAsync(
        string researchSessionId,
        string memoryType = "research_finding",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(researchSessionId))
        {
            return Array.Empty<ResearchFinding>();
        }

        IReadOnlyList<MemoryRecord> records = await _cosmosMemoryService
            .QueryBySessionAsync(researchSessionId, 2000, cancellationToken)
            .ConfigureAwait(false);

        var filtered = records
            .Where(record => string.Equals(record.Type, memoryType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            var reconstructed = ReconstructFindingsFromStoredArtifacts(records);
            if (reconstructed.Count == 0)
            {
                return Array.Empty<ResearchFinding>();
            }

            _logger.LogInformation(
                "Reconstructed {FindingCount} finding(s) for session {SessionId} using stored tool outputs.",
                reconstructed.Count,
                researchSessionId);
            return reconstructed;
        }

        var grouped = new Dictionary<string, List<MemoryRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in filtered)
        {
            string key = record.Metadata.TryGetValue("chunkParentId", out var parentId) && !string.IsNullOrWhiteSpace(parentId)
                ? parentId
                : record.Id;

            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = new List<MemoryRecord>();
                grouped[key] = bucket;
            }

            bucket.Add(record);
        }

        var results = new List<(ResearchFinding Finding, DateTimeOffset Timestamp)>(grouped.Count);

        foreach (var entry in grouped)
        {
            static int ResolveChunkIndex(MemoryRecord record)
            {
                if (record.Metadata.TryGetValue("chunkIndex", out var chunkValue) &&
                    int.TryParse(chunkValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    return parsed;
                }

                return 0;
            }

            var ordered = entry.Value
                .OrderBy(record => ResolveChunkIndex(record))
                .ThenBy(record => record.CreatedAtUtc)
                .ToList();

            var primary = ordered[0];
            string combinedContent = string.Join(
                Environment.NewLine + Environment.NewLine,
                ordered.Select(record => record.Content).Where(static chunk => !string.IsNullOrWhiteSpace(chunk)));

            string title = primary.Metadata.TryGetValue("title", out var storedTitle) && !string.IsNullOrWhiteSpace(storedTitle)
                ? storedTitle!
                : entry.Key;

            double confidence = primary.Metadata.TryGetValue("confidence", out var storedConfidence) &&
                double.TryParse(storedConfidence, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedConfidence)
                    ? parsedConfidence
                    : 0.5d;

            var citations = primary.Sources
                .Select(source => new SourceCitation(source.SourceId, source.Title, source.Url, source.Snippet))
                .GroupBy(citation => citation.SourceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var finding = new ResearchFinding
            {
                Id = entry.Key,
                Title = title,
                Content = combinedContent,
                Citations = citations,
                Confidence = confidence
            };

            results.Add((finding, primary.CreatedAtUtc));
        }

        return results
            .OrderByDescending(tuple => tuple.Timestamp)
            .Select(tuple => tuple.Finding)
            .ToList();
    }

    private IReadOnlyList<ResearchFinding> ReconstructFindingsFromStoredArtifacts(IReadOnlyList<MemoryRecord> records)
    {
        var toolOutputs = records
            .Where(record => record.Type.StartsWith("tool_output::", StringComparison.OrdinalIgnoreCase)
                              && !string.IsNullOrWhiteSpace(record.Content))
            .ToList();

        if (toolOutputs.Count == 0)
        {
            return Array.Empty<ResearchFinding>();
        }

        var grouped = toolOutputs
            .GroupBy(static record =>
            {
                if (record.Metadata.TryGetValue("agentTaskId", out var agentTaskId) && !string.IsNullOrWhiteSpace(agentTaskId))
                {
                    return agentTaskId;
                }

                if (record.Metadata.TryGetValue("instruction", out var instruction) && !string.IsNullOrWhiteSpace(instruction))
                {
                    return instruction;
                }

                return record.Type;
            }, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var findings = new List<ResearchFinding>(grouped.Count);

        foreach (var group in grouped)
        {
            var ordered = group
                .OrderBy(record => record.CreatedAtUtc)
                .ToList();

            var primary = ordered[^1];
            string title = ResolveFallbackTitle(primary);

            var contentBuilder = new StringBuilder();
            foreach (var record in ordered)
            {
                string chunk = record.Content.Trim();
                if (chunk.Length == 0)
                {
                    continue;
                }

                contentBuilder.AppendLine(chunk);
                contentBuilder.AppendLine();
            }

            string combinedContent = contentBuilder.ToString().Trim();
            if (combinedContent.Length == 0)
            {
                continue;
            }

            var citations = ordered
                .SelectMany(record => record.Sources.Select(source => (source, recordId: record.Id)))
                .Where(tuple => tuple.source is not null)
                .GroupBy(tuple =>
                    string.IsNullOrWhiteSpace(tuple.source.SourceId)
                        ? tuple.source.Url ?? tuple.source.Title ?? $"memory:{tuple.recordId}"
                        : tuple.source.SourceId,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var source = group.First().source;
                    string sourceId = string.IsNullOrWhiteSpace(source.SourceId) ? group.Key : source.SourceId;
                    return new SourceCitation(sourceId, source.Title, source.Url, source.Snippet);
                })
                .ToList();

            var finding = new ResearchFinding
            {
                Id = primary.Metadata.TryGetValue("agentTaskId", out var agentTaskId) && !string.IsNullOrWhiteSpace(agentTaskId)
                    ? agentTaskId
                    : primary.Id,
                Title = title,
                Content = combinedContent,
                Citations = citations,
                Confidence = ResolveFallbackConfidence(primary)
            };

            findings.Add(finding);
        }

        return findings
            .OrderByDescending(finding => finding.Confidence)
            .ThenByDescending(finding => finding.Content.Length)
            .ToList();
    }

    private static string ResolveFallbackTitle(MemoryRecord record)
    {
        if (record.Metadata.TryGetValue("instruction", out var instruction) && !string.IsNullOrWhiteSpace(instruction))
        {
            return instruction.Trim();
        }

        if (record.Metadata.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        if (record.Metadata.TryGetValue("tool", out var toolName) && !string.IsNullOrWhiteSpace(toolName))
        {
            return $"Output from {toolName.Trim()}";
        }

        return "Restored Finding";
    }

    private static double ResolveFallbackConfidence(MemoryRecord record)
    {
        if (record.Metadata.TryGetValue("confidence", out var confidenceValue)
            && double.TryParse(confidenceValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && parsed is >= 0 and <= 1)
        {
            return parsed;
        }

        return 0.35d;
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
        var placeholder = CreatePlaceholderEmbedding();
        var record = new MemoryRecord
        {
            Id = $"{researchSessionId}_branch_{branchId}",
            ResearchSessionId = researchSessionId,
            Type = "branch_state",
            Content = summary ?? question,
            Embedding = placeholder.Length > 0 ? placeholder : null,
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
        var placeholder = CreatePlaceholderEmbedding();
        var record = new MemoryRecord
        {
            Id = $"{researchSessionId}_session_state",
            ResearchSessionId = researchSessionId,
            Type = "session_state",
            Content = note ?? state,
            Embedding = placeholder.Length > 0 ? placeholder : null,
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
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<float>();
        }

        try
        {
            var vector = await _openAiService.GenerateEmbeddingAsync(
                new OpenAiEmbeddingRequest(_embeddingDeploymentName, content),
                cancellationToken).ConfigureAwait(false);

            if (vector is null || vector.Count == 0)
            {
                _logger.LogWarning(
                    "Embedding service returned no vector for content length {Length}. Using empty embedding.",
                    content.Length);
                return Array.Empty<float>();
            }

            int expectedDimensions = _cosmosMemoryService.VectorDimensions;
            if (expectedDimensions > 0 && vector.Count != expectedDimensions)
            {
                _logger.LogWarning(
                    "Embedding dimension mismatch for content length {Length}. Expected {Expected} but received {Actual}.",
                    content.Length,
                    expectedDimensions,
                    vector.Count);
                return Array.Empty<float>();
            }

            return vector.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Embedding generation failed for content length {Length}. Falling back to empty embedding.",
                content.Length);
            return Array.Empty<float>();
        }
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

            var embedding = await GenerateEmbeddingAsync(chunk, cancellationToken).ConfigureAwait(false);

            var record = new MemoryRecord
            {
                Id = chunkCount == 1 ? baseId : $"{baseId}_chunk_{index:D4}",
                ResearchSessionId = researchSessionId,
                Type = memoryType,
                Content = chunk,
                Embedding = embedding.Length > 0 ? embedding : null,
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
        int dimensions = _cosmosMemoryService.VectorDimensions;
        if (dimensions <= 0)
        {
            return Array.Empty<float>();
        }

        return new float[dimensions];
    }
}
