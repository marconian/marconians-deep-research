using System.Linq;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Services.Cosmos;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;

namespace Marconian.ResearchAgent.Memory;

public sealed class LongTermMemoryManager
{
    private readonly ICosmosMemoryService _cosmosMemoryService;
    private readonly IAzureOpenAiService _openAiService;
    private readonly string _embeddingDeploymentName;

    public LongTermMemoryManager(ICosmosMemoryService cosmosMemoryService, IAzureOpenAiService openAiService, string embeddingDeploymentName)
    {
        _cosmosMemoryService = cosmosMemoryService ?? throw new ArgumentNullException(nameof(cosmosMemoryService));
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _embeddingDeploymentName = string.IsNullOrWhiteSpace(embeddingDeploymentName)
            ? throw new ArgumentException("Embedding deployment name must be provided.", nameof(embeddingDeploymentName))
            : embeddingDeploymentName;
    }

    public async Task<MemoryRecord> StoreFindingAsync(
        string researchSessionId,
        ResearchFinding finding,
        string memoryType = "research_finding",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var embedding = await _openAiService.GenerateEmbeddingAsync(
            new OpenAiEmbeddingRequest(_embeddingDeploymentName, finding.Content),
            cancellationToken).ConfigureAwait(false);

        var record = new MemoryRecord
        {
            ResearchSessionId = researchSessionId,
            Type = memoryType,
            Content = finding.Content,
            Embedding = embedding.ToList(),
            Metadata = new Dictionary<string, string>
            {
                ["title"] = finding.Title,
                ["confidence"] = finding.Confidence.ToString("F2"),
            },
            Sources = finding.Citations
                .Select(citation => new MemorySourceReference(citation.SourceId, citation.Title, citation.Url, citation.Snippet))
                .ToList()
        };

        await _cosmosMemoryService.UpsertRecordAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<MemoryRecord> StoreDocumentAsync(
        string researchSessionId,
        string content,
        string memoryType,
        IEnumerable<SourceCitation> citations,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var embedding = await _openAiService.GenerateEmbeddingAsync(
            new OpenAiEmbeddingRequest(_embeddingDeploymentName, content),
            cancellationToken).ConfigureAwait(false);

        var record = new MemoryRecord
        {
            ResearchSessionId = researchSessionId,
            Type = memoryType,
            Content = content,
            Embedding = embedding.ToList(),
            Metadata = metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata),
            Sources = citations.Select(c => new MemorySourceReference(c.SourceId, c.Title, c.Url, c.Snippet)).ToList()
        };

        await _cosmosMemoryService.UpsertRecordAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public Task<IReadOnlyList<MemoryRecord>> GetRecentMemoriesAsync(string researchSessionId, int limit, CancellationToken cancellationToken = default)
        => _cosmosMemoryService.QueryBySessionAsync(researchSessionId, limit, cancellationToken);

    public async Task<IReadOnlyList<MemorySearchResult>> SearchRelevantAsync(
        string researchSessionId,
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var embedding = await _openAiService.GenerateEmbeddingAsync(
            new OpenAiEmbeddingRequest(_embeddingDeploymentName, query),
            cancellationToken).ConfigureAwait(false);

        return await _cosmosMemoryService.QuerySimilarAsync(researchSessionId, embedding, limit, cancellationToken).ConfigureAwait(false);
    }
}
