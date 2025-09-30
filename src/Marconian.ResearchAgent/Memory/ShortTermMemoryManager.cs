using System.Linq;
using System.Text;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;

namespace Marconian.ResearchAgent.Memory;

public sealed class ShortTermMemoryManager
{
    private readonly List<ShortTermMemoryEntry> _entries = new();
    private readonly IAzureOpenAiService _openAiService;
    private readonly IRedisCacheService? _cacheService;
    private readonly string _deploymentName;
    private readonly string _cacheKey;
    private readonly int _maxEntries;
    private readonly int _summaryBatchSize;

    public ShortTermMemoryManager(
        string agentId,
        string researchSessionId,
        IAzureOpenAiService openAiService,
        string deploymentName,
        IRedisCacheService? cacheService = null,
        int maxEntries = 40,
        int summaryBatchSize = 6)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(researchSessionId);
        ArgumentNullException.ThrowIfNull(openAiService);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        _openAiService = openAiService;
        _cacheService = cacheService;
        _deploymentName = deploymentName;
        _cacheKey = $"stm:{researchSessionId}:{agentId}";
        _maxEntries = Math.Max(summaryBatchSize + 2, maxEntries);
        _summaryBatchSize = Math.Clamp(summaryBatchSize, 3, Math.Max(3, maxEntries / 2));
    }

    public IReadOnlyList<ShortTermMemoryEntry> Entries => _entries.AsReadOnly();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_cacheService is null)
        {
            return;
        }

        var cached = await _cacheService.GetAsync<List<ShortTermMemoryEntry>>(_cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached is { Count: > 0 })
        {
            _entries.Clear();
            _entries.AddRange(cached.OrderBy(entry => entry.TimestampUtc));
        }
    }

    public async Task AppendAsync(string role, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _entries.Add(new ShortTermMemoryEntry
        {
            Role = role,
            Content = content,
            TimestampUtc = DateTimeOffset.UtcNow,
            IsSummary = false
        });

        if (_entries.Count > _maxEntries)
        {
            await SummarizeOldestEntriesAsync(cancellationToken).ConfigureAwait(false);
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public string BuildPromptContext()
    {
        var builder = new StringBuilder();
        foreach (var entry in _entries.OrderBy(entry => entry.TimestampUtc))
        {
            builder.Append('[').Append(entry.Role).Append("] ");
            builder.AppendLine(entry.Content);
        }

        return builder.ToString();
    }

    private async Task SummarizeOldestEntriesAsync(CancellationToken cancellationToken)
    {
        int batchCount = Math.Min(_summaryBatchSize, Math.Max(1, _entries.Count - (_maxEntries - _summaryBatchSize)));
        if (batchCount <= 0)
        {
            return;
        }

        var itemsToSummarize = _entries.Take(batchCount).ToList();
        var summaryPrompt = new StringBuilder();
        summaryPrompt.AppendLine("Summarize the following agent interaction history into key bullet points that maintain factual accuracy.");
        summaryPrompt.AppendLine("Focus on decisions, context, and follow-up questions.");
        summaryPrompt.AppendLine();

        foreach (var entry in itemsToSummarize)
        {
            summaryPrompt.Append('[').Append(entry.Role).Append(']').Append(' ');
            summaryPrompt.AppendLine(entry.Content);
        }

        var request = new OpenAiChatRequest(
            SystemPrompt: "You compress agent working memories without losing vital details.",
            Messages: new[]
            {
                new OpenAiChatMessage("user", summaryPrompt.ToString())
            },
            DeploymentName: _deploymentName,
            MaxOutputTokens: 256,
            Temperature: 0.1f
        );

        string summary = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        _entries.RemoveRange(0, batchCount);
        _entries.Insert(0, new ShortTermMemoryEntry
        {
            Role = "summary",
            Content = summary,
            TimestampUtc = DateTimeOffset.UtcNow,
            IsSummary = true
        });
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (_cacheService is null)
        {
            return;
        }

        await _cacheService.SetAsync(_cacheKey, _entries, TimeSpan.FromHours(6), cancellationToken).ConfigureAwait(false);
    }
}
