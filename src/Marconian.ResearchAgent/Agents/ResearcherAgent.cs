using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Marconian.ResearchAgent.Tools;
using Marconian.ResearchAgent.Tracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Agents;

public sealed class ResearcherAgent : IAgent
{
    private readonly IAzureOpenAiService _openAiService;
    private readonly LongTermMemoryManager _longTermMemoryManager;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly string _chatDeploymentName;
    private readonly ICacheService? _cacheService;
    private readonly ILogger<ResearcherAgent> _logger;
    private readonly ILogger<ShortTermMemoryManager> _shortTermLogger;
    private readonly ResearchFlowTracker? _flowTracker;
    private readonly ResearcherOptions _options;
    private readonly ShortTermMemoryOptions _shortTermOptions;
    private readonly ResearcherParallelismOptions _parallelismOptions;

    public ResearcherAgent(
        IAzureOpenAiService openAiService,
        LongTermMemoryManager longTermMemoryManager,
        IEnumerable<ITool> tools,
        string chatDeploymentName,
        ICacheService? cacheService = null,
        ResearchFlowTracker? flowTracker = null,
        ILogger<ResearcherAgent>? logger = null,
        ILogger<ShortTermMemoryManager>? shortTermLogger = null,
        ResearcherOptions? options = null,
        ShortTermMemoryOptions? shortTermOptions = null)
    {
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _longTermMemoryManager = longTermMemoryManager ?? throw new ArgumentNullException(nameof(longTermMemoryManager));
        _tools = tools?.ToList() ?? throw new ArgumentNullException(nameof(tools));
        _chatDeploymentName = string.IsNullOrWhiteSpace(chatDeploymentName)
            ? throw new ArgumentException("Chat deployment name must be provided.", nameof(chatDeploymentName))
            : chatDeploymentName;
        _cacheService = cacheService;
        _flowTracker = flowTracker;
        _logger = logger ?? NullLogger<ResearcherAgent>.Instance;
        _shortTermLogger = shortTermLogger ?? NullLogger<ShortTermMemoryManager>.Instance;
        _options = SanitizeOptions(options);
        _shortTermOptions = SanitizeShortTermOptions(shortTermOptions);
        _parallelismOptions = _options.Parallelism;
    }

    private static ResearcherOptions SanitizeOptions(ResearcherOptions? options)
    {
        var resolved = options is null
            ? new ResearcherOptions()
            : new ResearcherOptions
            {
                MaxSearchIterations = options.MaxSearchIterations,
                MaxContinuationQueries = options.MaxContinuationQueries,
                RelatedMemoryTake = options.RelatedMemoryTake,
                Parallelism = options.Parallelism is null
                    ? new ResearcherParallelismOptions()
                    : new ResearcherParallelismOptions
                    {
                        NavigatorDegreeOfParallelism = options.Parallelism.NavigatorDegreeOfParallelism,
                        ScraperDegreeOfParallelism = options.Parallelism.ScraperDegreeOfParallelism,
                        FileReaderDegreeOfParallelism = options.Parallelism.FileReaderDegreeOfParallelism
                    }
            };

        resolved.MaxSearchIterations = Math.Max(1, resolved.MaxSearchIterations);
        resolved.MaxContinuationQueries = Math.Max(0, resolved.MaxContinuationQueries);
        resolved.RelatedMemoryTake = Math.Max(0, resolved.RelatedMemoryTake);
        resolved.Parallelism = SanitizeParallelism(resolved.Parallelism);
        return resolved;
    }

    private static ResearcherParallelismOptions SanitizeParallelism(ResearcherParallelismOptions? options)
    {
        var resolved = options is null
            ? new ResearcherParallelismOptions()
            : new ResearcherParallelismOptions
            {
                NavigatorDegreeOfParallelism = options.NavigatorDegreeOfParallelism,
                ScraperDegreeOfParallelism = options.ScraperDegreeOfParallelism,
                FileReaderDegreeOfParallelism = options.FileReaderDegreeOfParallelism
            };

        resolved.NavigatorDegreeOfParallelism = Math.Max(1, resolved.NavigatorDegreeOfParallelism);
        resolved.ScraperDegreeOfParallelism = Math.Max(1, resolved.ScraperDegreeOfParallelism);
        resolved.FileReaderDegreeOfParallelism = Math.Max(1, resolved.FileReaderDegreeOfParallelism);
        return resolved;
    }

    private static ShortTermMemoryOptions SanitizeShortTermOptions(ShortTermMemoryOptions? options)
    {
        if (options is null)
        {
            return new ShortTermMemoryOptions();
        }

        return new ShortTermMemoryOptions
        {
            MaxEntries = options.MaxEntries,
            SummaryBatchSize = options.SummaryBatchSize,
            CacheTtlHours = options.CacheTtlHours
        };
    }

    private static async Task AppendToShortTermAsync(
        ShortTermMemoryManager shortTermMemory,
        SemaphoreSlim shortTermLock,
        string role,
        string content,
        CancellationToken cancellationToken)
    {
        await shortTermLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await shortTermMemory.AppendAsync(role, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            shortTermLock.Release();
        }
    }

    public async Task<AgentExecutionResult> ExecuteTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        _logger.LogInformation("Researcher {TaskId} starting objective '{Objective}'.", task.TaskId, task.Objective);

        var shortTermMemory = new ShortTermMemoryManager(
            agentId: task.TaskId,
            researchSessionId: task.ResearchSessionId,
            _openAiService,
            _chatDeploymentName,
            _cacheService,
            options: _shortTermOptions,
            logger: _shortTermLogger);

        await shortTermMemory.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var shortTermLock = new SemaphoreSlim(1, 1);
        await AppendToShortTermAsync(shortTermMemory, shortTermLock, "user", task.Objective ?? string.Empty, cancellationToken).ConfigureAwait(false);

        var toolOutputs = new List<ToolExecutionResult>();
        var errors = new List<string>();
        var toolOutputsLock = new object();
        var errorsLock = new object();
        var flaggedResourceUrls = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var navigatorVisitedUrls = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var scrapedPageUrls = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var processedFileUrls = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        int memoryTake = Math.Max(0, _options.RelatedMemoryTake);
        IReadOnlyList<MemorySearchResult> relatedMemories = await _longTermMemoryManager
            .SearchRelevantAsync(task.ResearchSessionId, task.Objective ?? string.Empty, memoryTake, cancellationToken)
            .ConfigureAwait(false);

        if (relatedMemories.Count > 0)
        {
            string memorySummary = string.Join('\n', relatedMemories.Select(m => $"- {m.Record.Content}"));
            await AppendToShortTermAsync(shortTermMemory, shortTermLock, "memory", memorySummary, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordBranchNote(task.TaskId, $"Loaded {relatedMemories.Count} related memories.");
            _logger.LogDebug("Loaded {Count} related memories into short-term context for task {TaskId}.", relatedMemories.Count, task.TaskId);
        }

        var executedQueryOrder = new List<string>();
        var executedQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextQuery = task.Objective;
        int iteration = 0;

        if (TryGetTool<WebSearchTool>(out _))
        {
            while (!string.IsNullOrWhiteSpace(nextQuery) && iteration < _options.MaxSearchIterations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                iteration++;
                string query = nextQuery!.Trim();

                if (!executedQueries.Add(query))
                {
                    _logger.LogDebug("Skipping duplicate search query '{Query}' for task {TaskId}.", query, task.TaskId);
                }
                else
                {
                    executedQueryOrder.Add(query);
                    await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", $"---\nSearch iteration {iteration}: {query}", cancellationToken).ConfigureAwait(false);

                    await RunSearchIterationAsync(
                        task,
                        query,
                        iteration,
                        shortTermMemory,
                        shortTermLock,
                        navigatorVisitedUrls,
                        scrapedPageUrls,
                        processedFileUrls,
                        flaggedResourceUrls,
                        toolOutputs,
                        errors,
                        toolOutputsLock,
                        errorsLock,
                        cancellationToken).ConfigureAwait(false);
                }

                if (iteration >= _options.MaxSearchIterations)
                {
                    nextQuery = null;
                    break;
                }

                IterationDecision decision = await DecideNextSearchActionAsync(
                        task,
                        executedQueryOrder,
                        toolOutputs,
                        relatedMemories,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (decision.Action == IterationAction.Continue && decision.Queries.Count > 0)
                {
                    string? candidate = decision.Queries
                        .FirstOrDefault(q => !string.IsNullOrWhiteSpace(q) && !executedQueries.Contains(q, StringComparer.OrdinalIgnoreCase));

                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", $"Queuing additional search: {candidate} ({decision.Reason ?? "no reason provided"}).", cancellationToken).ConfigureAwait(false);
                        nextQuery = candidate;
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(decision.Reason))
                {
                    await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", $"Stopping search iterations: {decision.Reason}", cancellationToken).ConfigureAwait(false);
                }

                nextQuery = null;
            }
        }
        else
        {
            await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", "Search tool unavailable; proceeding with provided materials only.", cancellationToken).ConfigureAwait(false);
        }

        if ((task.Parameters.ContainsKey("imageFileId") || task.Parameters.ContainsKey("imageUrl")) && TryGetTool<ImageReaderTool>(out var imageReader))
        {
            var imageParams = new Dictionary<string, string>();
            if (task.Parameters.TryGetValue("imageFileId", out var imageFileId))
            {
                imageParams["fileId"] = imageFileId;
            }
            if (task.Parameters.TryGetValue("imageUrl", out var imageUrl))
            {
                imageParams["url"] = imageUrl;
            }

            await ExecuteToolAsync(
                task,
                imageReader,
                CreateContext(task, imageParams),
                toolOutputs,
                errors,
                shortTermMemory,
                shortTermLock,
                toolOutputsLock,
                errorsLock,
                cancellationToken).ConfigureAwait(false);
        }

        string aggregatedEvidence = BuildEvidenceSummary(toolOutputs, relatedMemories);
    await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", aggregatedEvidence, cancellationToken).ConfigureAwait(false);

        var synthesisRequest = new OpenAiChatRequest(
            SystemPrompt: SystemPrompts.Researcher.Analyst,
            Messages: new[]
            {
                new OpenAiChatMessage(
                    "user",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        SystemPrompts.Templates.Researcher.AnalystInstruction,
                        task.Objective ?? string.Empty,
                        aggregatedEvidence))
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 400);

        string summary = await _openAiService.GenerateTextAsync(synthesisRequest, cancellationToken).ConfigureAwait(false);
    await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", summary, cancellationToken).ConfigureAwait(false);

        double confidence = InferConfidence(summary);
        var finding = new ResearchFinding
        {
            Title = task.Objective ?? string.Empty,
            Content = summary,
            Citations = CollectCitations(toolOutputs, relatedMemories),
            Confidence = confidence
        };

        await _longTermMemoryManager.StoreFindingAsync(task.ResearchSessionId, finding, cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Researcher {TaskId} completed with confidence {Confidence:F2}.", task.TaskId, confidence);

        return new AgentExecutionResult
        {
            Success = true,
            Summary = summary,
            Findings = new List<ResearchFinding> { finding },
            ToolOutputs = toolOutputs,
            Errors = errors
        };
    }

    private async Task RunSearchIterationAsync(
        AgentTask task,
        string query,
        int iteration,
        ShortTermMemoryManager shortTermMemory,
        SemaphoreSlim shortTermLock,
        ConcurrentDictionary<string, byte> navigatorVisitedUrls,
        ConcurrentDictionary<string, byte> scrapedPageUrls,
        ConcurrentDictionary<string, byte> processedFileUrls,
        ConcurrentDictionary<string, byte> flaggedResourceUrls,
        List<ToolExecutionResult> toolOutputs,
        List<string> errors,
        object toolOutputsLock,
        object errorsLock,
        CancellationToken cancellationToken)
    {
        if (!TryGetTool<WebSearchTool>(out var searchTool))
        {
            await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", "Search capability unavailable; stopping iteration early.", cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Search tool not registered; cannot execute iteration {Iteration} for task {TaskId}.", iteration, task.TaskId);
            return;
        }

        ToolExecutionResult searchResult = await ExecuteToolAsync(
            task,
            searchTool,
            CreateContext(task, new Dictionary<string, string>
            {
                ["query"] = query
            }),
            toolOutputs,
            errors,
            shortTermMemory,
            shortTermLock,
            toolOutputsLock,
            errorsLock,
            cancellationToken).ConfigureAwait(false);

        if (!searchResult.Success)
        {
            await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", $"Iteration {iteration} search for '{query}' failed: {searchResult.ErrorMessage ?? "unknown error"}.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var baseCitations = searchResult.Citations
            .Where(static citation => !string.IsNullOrWhiteSpace(citation.Url))
            .DistinctBy(static citation => citation.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (baseCitations.Count == 0)
        {
            await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", "Search returned no actionable results; moving to next iteration.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var citationsForFollowUp = baseCitations.Take(3).ToList();
        var flaggedResourceTuples = new ConcurrentBag<(int Index, FlaggedResource Resource)>();

        if (TryGetTool<ComputerUseNavigatorTool>(out var navigatorTool))
        {
            ExplorationSelection selection = await SelectCitationsForExplorationAsync(task, searchResult, cancellationToken).ConfigureAwait(false);
            await PersistSelectionAsync(task, selection, cancellationToken).ConfigureAwait(false);

            if (selection.Citations.Count > 0)
            {
                citationsForFollowUp = selection.Citations
                    .Where(static citation => !string.IsNullOrWhiteSpace(citation.Url))
                    .DistinctBy(static citation => citation.Url, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            string selectionMessage = citationsForFollowUp.Count == 0
                ? "No search results selected for computer-use exploration."
                : $"Selected {citationsForFollowUp.Count} search result(s) for computer-use exploration.";

            if (!string.IsNullOrWhiteSpace(selection.Notes))
            {
                selectionMessage += $" Rationale: {selection.Notes}";
            }

            await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", selectionMessage, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordBranchNote(task.TaskId, selectionMessage);

            int navigatorDegree = Math.Max(1, _parallelismOptions.NavigatorDegreeOfParallelism);
            using var navigatorSemaphore = new SemaphoreSlim(navigatorDegree, navigatorDegree);
            var navigatorTasks = new List<Task>();

            for (int index = 0; index < citationsForFollowUp.Count; index++)
            {
                var citation = citationsForFollowUp[index];
                if (string.IsNullOrWhiteSpace(citation.Url))
                {
                    continue;
                }

                if (!navigatorVisitedUrls.TryAdd(citation.Url, 0))
                {
                    _logger.LogDebug("Skipping navigator revisit for {Url}.", citation.Url);
                    continue;
                }

                navigatorTasks.Add(ProcessNavigatorAsync(citation, index));
            }

            await Task.WhenAll(navigatorTasks).ConfigureAwait(false);

            async Task ProcessNavigatorAsync(SourceCitation citation, int citationIndex)
            {
                await navigatorSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var navParameters = new Dictionary<string, string>
                    {
                        ["url"] = citation.Url!,
                        ["objective"] = task.Objective ?? string.Empty
                    };

                    ToolExecutionResult navResult = await ExecuteToolAsync(
                        task,
                        navigatorTool,
                        CreateContext(task, navParameters),
                        toolOutputs,
                        errors,
                        shortTermMemory,
                        shortTermLock,
                        toolOutputsLock,
                        errorsLock,
                        cancellationToken).ConfigureAwait(false);

                    if (navResult.FlaggedResources.Count == 0)
                    {
                        return;
                    }

                    foreach (var resource in navResult.FlaggedResources)
                    {
                        if (string.IsNullOrWhiteSpace(resource.Url))
                        {
                            continue;
                        }

                        if (flaggedResourceUrls.TryAdd(resource.Url, 0))
                        {
                            flaggedResourceTuples.Add((citationIndex, resource));
                        }
                    }
                }
                finally
                {
                    navigatorSemaphore.Release();
                }
            }
        }
        else
        {
            const string message = "Computer-use navigator unavailable; will proceed directly to scraping and downloads.";
            await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", message, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordBranchNote(task.TaskId, message);
        }

        var splitCitations = SplitCitations(citationsForFollowUp);
        var citationsForPages = splitCitations.Pages;
        var documentCitations = splitCitations.Documents;

        var flaggedResources = flaggedResourceTuples
            .OrderBy(static tuple => tuple.Index)
            .Select(static tuple => tuple.Resource)
            .ToList();

        if (flaggedResources.Count > 0)
        {
            var noteBuilder = new StringBuilder();
            noteBuilder.AppendLine($"Navigator flagged {flaggedResources.Count} resource(s) for deeper review.");

            foreach (var resource in flaggedResources)
            {
                FlaggedResourceType displayType = ShouldTreatAsBinaryResource(resource) ? FlaggedResourceType.File : resource.Type;
                noteBuilder.Append("- ").Append(displayType).Append(':');
                noteBuilder.Append(' ').Append(string.IsNullOrWhiteSpace(resource.Title) ? resource.Url : resource.Title);
                if (!string.IsNullOrWhiteSpace(resource.Url))
                {
                    noteBuilder.Append(" -> ").Append(resource.Url);
                }
                if (!string.IsNullOrWhiteSpace(resource.Notes))
                {
                    noteBuilder.Append(" (" + resource.Notes + ")");
                }
                noteBuilder.AppendLine();
            }

            string flaggedSummary = noteBuilder.ToString().TrimEnd();
            await AppendToShortTermAsync(shortTermMemory, shortTermLock, "assistant", flaggedSummary, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordBranchNote(task.TaskId, flaggedSummary);
        }

        var flaggedPageCitations = new List<SourceCitation>();
        var flaggedPageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flaggedFileResources = new List<FlaggedResource>();
        var flaggedFileUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in flaggedResources)
        {
            if (string.IsNullOrWhiteSpace(resource.Url))
            {
                continue;
            }

            if (ShouldTreatAsBinaryResource(resource))
            {
                if (flaggedFileUrls.Add(resource.Url))
                {
                    flaggedFileResources.Add(resource);
                }
            }
            else if (flaggedPageUrls.Add(resource.Url))
            {
                flaggedPageCitations.Add(new SourceCitation(
                    $"flag:{Guid.NewGuid():N}",
                    string.IsNullOrWhiteSpace(resource.Title) ? resource.Url : resource.Title,
                    resource.Url,
                    resource.Notes ?? resource.MimeType ?? "Flagged during computer-use exploration"));
            }
        }

        if (TryGetTool<WebScraperTool>(out var scraperTool))
        {
            var scrapeCandidates = new List<SourceCitation>();
            foreach (var citation in flaggedPageCitations.Concat(citationsForPages))
            {
                if (string.IsNullOrWhiteSpace(citation.Url))
                {
                    continue;
                }

                if (!scrapedPageUrls.TryAdd(citation.Url, 0))
                {
                    continue;
                }

                scrapeCandidates.Add(citation);
                if (scrapeCandidates.Count >= 5)
                {
                    break;
                }
            }

            int scraperDegree = Math.Max(1, _parallelismOptions.ScraperDegreeOfParallelism);
            using var scraperSemaphore = new SemaphoreSlim(scraperDegree, scraperDegree);
            var scraperTasks = new List<Task>();

            foreach (var citation in scrapeCandidates)
            {
                scraperTasks.Add(ProcessScrapeAsync(citation));
            }

            await Task.WhenAll(scraperTasks).ConfigureAwait(false);

            async Task ProcessScrapeAsync(SourceCitation citation)
            {
                await scraperSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var scrapeParameters = new Dictionary<string, string>
                    {
                        ["url"] = citation.Url!
                    };

                    if (task.Parameters.TryGetValue("render", out var renderFlag))
                    {
                        scrapeParameters["render"] = renderFlag;
                    }

                    await ExecuteToolAsync(
                        task,
                        scraperTool,
                        CreateContext(task, scrapeParameters),
                        toolOutputs,
                        errors,
                        shortTermMemory,
                        shortTermLock,
                        toolOutputsLock,
                        errorsLock,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    scraperSemaphore.Release();
                }
            }
        }

        if (TryGetTool<FileReaderTool>(out var fileReader))
        {
            var fileReadRequests = new List<Dictionary<string, string>>();
            var requestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (task.Parameters.Keys.Any(key => key is "fileId" or "url" or "path"))
            {
                string? requestKey = null;
                if (task.Parameters.TryGetValue("url", out var fileUrl) && !string.IsNullOrWhiteSpace(fileUrl))
                {
                    requestKey = fileUrl;
                }
                else if (task.Parameters.TryGetValue("fileId", out var fileId) && !string.IsNullOrWhiteSpace(fileId))
                {
                    requestKey = $"fileId:{fileId}";
                }

                if (requestKey is null || processedFileUrls.TryAdd(requestKey, 0))
                {
                    fileReadRequests.Add(new Dictionary<string, string>(task.Parameters));
                    if (requestKey is not null)
                    {
                        requestKeys.Add(requestKey);
                    }
                }
            }

            foreach (var resource in flaggedFileResources)
            {
                if (string.IsNullOrWhiteSpace(resource.Url) || !processedFileUrls.TryAdd(resource.Url, 0) || !requestKeys.Add(resource.Url))
                {
                    continue;
                }

                var parameters = new Dictionary<string, string>
                {
                    ["url"] = resource.Url,
                    ["origin"] = "computer-use"
                };

                if (!string.IsNullOrWhiteSpace(resource.MimeType))
                {
                    parameters["contentTypeHint"] = resource.MimeType!;
                }

                if (!string.IsNullOrWhiteSpace(resource.Notes))
                {
                    parameters["note"] = resource.Notes!;
                }

                fileReadRequests.Add(parameters);
            }

            foreach (var citation in documentCitations)
            {
                if (string.IsNullOrWhiteSpace(citation.Url) || !processedFileUrls.TryAdd(citation.Url, 0) || !requestKeys.Add(citation.Url))
                {
                    continue;
                }

                var parameters = new Dictionary<string, string>
                {
                    ["url"] = citation.Url,
                    ["origin"] = "search-result",
                    ["contentTypeHint"] = "application/pdf"
                };

                if (!string.IsNullOrWhiteSpace(citation.Title))
                {
                    parameters["note"] = citation.Title!;
                }
                else if (!string.IsNullOrWhiteSpace(citation.Snippet))
                {
                    parameters["note"] = citation.Snippet!;
                }

                fileReadRequests.Add(parameters);
            }

            int fileReaderDegree = Math.Max(1, _parallelismOptions.FileReaderDegreeOfParallelism);
            using var fileReaderSemaphore = new SemaphoreSlim(fileReaderDegree, fileReaderDegree);
            var fileReaderTasks = new List<Task>();

            foreach (var request in fileReadRequests)
            {
                fileReaderTasks.Add(ProcessFileReadAsync(request));
            }

            await Task.WhenAll(fileReaderTasks).ConfigureAwait(false);

            async Task ProcessFileReadAsync(Dictionary<string, string> request)
            {
                await fileReaderSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ExecuteToolAsync(
                        task,
                        fileReader,
                        CreateContext(task, request),
                        toolOutputs,
                        errors,
                        shortTermMemory,
                        shortTermLock,
                        toolOutputsLock,
                        errorsLock,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    fileReaderSemaphore.Release();
                }
            }
        }
    }

    private async Task<IterationDecision> DecideNextSearchActionAsync(
        AgentTask task,
        IReadOnlyList<string> executedQueries,
        IReadOnlyList<ToolExecutionResult> toolOutputs,
        IReadOnlyList<MemorySearchResult> relatedMemories,
        CancellationToken cancellationToken)
    {
        if (executedQueries.Count >= _options.MaxSearchIterations)
        {
            return new IterationDecision(IterationAction.Stop, Array.Empty<string>(), "Reached search iteration limit.");
        }

        string evidenceSnapshot = BuildEvidenceSummary(toolOutputs, relatedMemories);
        if (evidenceSnapshot.Length > 2000)
        {
            evidenceSnapshot = evidenceSnapshot[..2000] + "…";
        }

        var prompt = new StringBuilder();
        prompt.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            SystemPrompts.Templates.Researcher.SupervisorObjectiveLine,
            task.Objective ?? string.Empty));
        prompt.AppendLine(SystemPrompts.Templates.Researcher.SupervisorHistoryHeader);
        for (int index = 0; index < executedQueries.Count; index++)
        {
            prompt.AppendLine($"{index + 1}. {executedQueries[index]}");
        }

        prompt.AppendLine();
        prompt.AppendLine(SystemPrompts.Templates.Researcher.SupervisorEvidenceHeader);
        prompt.AppendLine(evidenceSnapshot);
        prompt.AppendLine();
        prompt.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            SystemPrompts.Templates.Researcher.SupervisorDecisionInstruction,
            _options.MaxContinuationQueries));

        var decisionRequest = new OpenAiChatRequest(
            SystemPrompt: SystemPrompts.Researcher.Supervisor,
            Messages: new[]
            {
                new OpenAiChatMessage("user", prompt.ToString())
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 300);

        string response = await _openAiService.GenerateTextAsync(decisionRequest, cancellationToken).ConfigureAwait(false);
        return ParseIterationDecision(response);
    }

    private IterationDecision ParseIterationDecision(string response)
    {
        string? jsonPayload = ExtractJsonSegment(response);
        if (jsonPayload is null)
        {
            return new IterationDecision(IterationAction.Stop, Array.Empty<string>(), "No structured response.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonPayload);
            string? actionText = document.RootElement.TryGetProperty("action", out JsonElement actionElement) && actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()
                : null;

            IterationAction action = string.Equals(actionText, "continue", StringComparison.OrdinalIgnoreCase)
                ? IterationAction.Continue
                : IterationAction.Stop;

            var queries = new List<string>();
            if (document.RootElement.TryGetProperty("queries", out JsonElement queriesElement) && queriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement queryElement in queriesElement.EnumerateArray())
                {
                    if (queryElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? query = queryElement.GetString();
                    if (string.IsNullOrWhiteSpace(query) || queries.Count >= _options.MaxContinuationQueries)
                    {
                        continue;
                    }

                    if (!queries.Contains(query, StringComparer.OrdinalIgnoreCase))
                    {
                        queries.Add(query.Trim());
                    }
                }
            }

            string? reason = null;
            if (document.RootElement.TryGetProperty("reason", out JsonElement reasonElement) && reasonElement.ValueKind == JsonValueKind.String)
            {
                reason = reasonElement.GetString();
            }

            if (action == IterationAction.Stop)
            {
                return new IterationDecision(action, Array.Empty<string>(), reason);
            }

            return new IterationDecision(action, queries, reason);
        }
        catch (JsonException)
        {
            return new IterationDecision(IterationAction.Stop, Array.Empty<string>(), "Failed to parse continuation response.");
        }
    }

    private enum IterationAction
    {
        Stop,
        Continue
    }

    private sealed record IterationDecision(IterationAction Action, IReadOnlyList<string> Queries, string? Reason);

    private async Task PersistSelectionAsync(AgentTask task, ExplorationSelection selection, CancellationToken cancellationToken)
    {
        if (selection.Citations.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Selected citations for objective: {task.Objective}");

        if (!string.IsNullOrWhiteSpace(selection.Notes))
        {
            builder.AppendLine($"Notes: {selection.Notes}");
            builder.AppendLine();
        }

        for (int index = 0; index < selection.Citations.Count; index++)
        {
            var citation = selection.Citations[index];
            builder.AppendLine($"{index + 1}. {citation.Title ?? citation.Url ?? citation.SourceId}");
            if (!string.IsNullOrWhiteSpace(citation.Url))
            {
                builder.AppendLine(citation.Url);
            }
            if (!string.IsNullOrWhiteSpace(citation.Snippet))
            {
                builder.AppendLine(citation.Snippet);
            }
            builder.AppendLine();
        }

        var metadata = new Dictionary<string, string>
        {
            ["agentTaskId"] = task.TaskId,
            ["selectionCount"] = selection.Citations.Count.ToString(CultureInfo.InvariantCulture),
            ["tool"] = "SearchSelection"
        };

        if (!string.IsNullOrWhiteSpace(selection.Notes))
        {
            metadata["selectionNotes"] = selection.Notes!;
        }

        await _longTermMemoryManager.StoreDocumentAsync(
            task.ResearchSessionId,
            builder.ToString().TrimEnd(),
            "search_selection",
            selection.Citations,
            metadata,
            cancellationToken).ConfigureAwait(false);
    }

    private ToolExecutionContext CreateContext(AgentTask task, Dictionary<string, string> parameters)
        => new()
        {
            AgentId = task.TaskId,
            ResearchSessionId = task.ResearchSessionId,
            Instruction = task.Objective ?? string.Empty,
            Parameters = parameters
        };

    private static (List<SourceCitation> Pages, List<SourceCitation> Documents) SplitCitations(IEnumerable<SourceCitation> citations)
    {
        var pages = new List<SourceCitation>();
        var documents = new List<SourceCitation>();

        foreach (var citation in citations)
        {
            if (IsLikelyPdf(citation.Url))
            {
                documents.Add(citation);
            }
            else
            {
                pages.Add(citation);
            }
        }

        return (pages, documents);
    }

    private static bool ShouldTreatAsBinaryResource(FlaggedResource resource)
    {
        if (resource.Type is FlaggedResourceType.File or FlaggedResourceType.Download)
        {
            return true;
        }

        return IsLikelyPdf(resource.Url, resource.MimeType);
    }

    private static bool IsLikelyPdf(string? url, string? mimeType = null)
    {
        if (!string.IsNullOrWhiteSpace(mimeType) && mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string path = uri.AbsolutePath;
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        else if (url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task<ToolExecutionResult> ExecuteToolAsync(
        AgentTask task,
        ITool tool,
        ToolExecutionContext context,
        List<ToolExecutionResult> outputs,
        List<string> errors,
        ShortTermMemoryManager shortTermMemory,
        SemaphoreSlim shortTermLock,
        object outputsLock,
        object errorsLock,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;
        ToolExecutionResult result = new()
        {
            ToolName = tool.Name,
            Success = false,
            ErrorMessage = "Tool execution deferred"
        };

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _logger.LogDebug("Executing tool {Tool} for task {TaskId}, attempt {Attempt}.", tool.Name, task.TaskId, attempt);
                result = await tool.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new ToolExecutionResult
                {
                    ToolName = tool.Name,
                    Success = false,
                    ErrorMessage = ex.Message
                };
                _logger.LogWarning(ex, "Tool {Tool} threw an exception on task {TaskId}.", tool.Name, task.TaskId);
            }

            if (result.Success || attempt == maxAttempts)
            {
                break;
            }

            TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
        }

        lock (outputsLock)
        {
            outputs.Add(result);
        }
        _flowTracker?.RecordToolExecution(task.TaskId, result);

        if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
        {
            await _longTermMemoryManager.StoreDocumentAsync(
                task.ResearchSessionId,
                result.Output,
                $"tool_output::{tool.Name}",
                result.Citations,
                new Dictionary<string, string>
                {
                    ["tool"] = tool.Name,
                    ["agentTaskId"] = task.TaskId,
                    ["instruction"] = context.Instruction
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Persisted output from tool {Tool} for task {TaskId}.", tool.Name, task.TaskId);
        }

        await AppendToShortTermAsync(shortTermMemory, shortTermLock, "tool", $"{tool.Name} => {(result.Success ? "success" : "failure")}", cancellationToken).ConfigureAwait(false);
        if (!result.Success && result.ErrorMessage is not null)
        {
            lock (errorsLock)
            {
                errors.Add($"{tool.Name}: {result.ErrorMessage}");
            }
            _flowTracker?.RecordBranchNote(task.TaskId, $"{tool.Name}: {result.ErrorMessage}");
            _logger.LogWarning("Tool {Tool} failed for task {TaskId}: {Error}.", tool.Name, task.TaskId, result.ErrorMessage);
        }
        else
        {
            _logger.LogInformation("Tool {Tool} completed for task {TaskId} (success={Success}).", tool.Name, task.TaskId, result.Success);
        }

        return result;
    }

    private bool TryGetTool<TTool>([NotNullWhen(true)] out TTool? tool)
        where TTool : class, ITool
    {
        tool = _tools.OfType<TTool>().FirstOrDefault();
        return tool is not null;
    }

    private static string BuildEvidenceSummary(IEnumerable<ToolExecutionResult> toolOutputs, IReadOnlyList<MemorySearchResult> relatedMemories)
    {
        var builder = new StringBuilder();
        foreach (var output in toolOutputs.Where(o => o.Success && !string.IsNullOrWhiteSpace(o.Output)))
        {
            builder.AppendLine($"Source: {output.ToolName}");
            builder.AppendLine(output.Output.Length > 1200 ? output.Output[..1200] + "…" : output.Output);
            builder.AppendLine();
        }

        if (relatedMemories.Count > 0)
        {
            builder.AppendLine(SystemPrompts.Templates.Common.RelevantMemoriesHeader);
            foreach (var memory in relatedMemories)
            {
                builder.AppendLine($"- {memory.Record.Content}");
            }
        }

        return builder.Length == 0
            ? SystemPrompts.Templates.Common.NoEvidenceCollected
            : builder.ToString();
    }

    private static List<SourceCitation> CollectCitations(IEnumerable<ToolExecutionResult> toolOutputs, IReadOnlyList<MemorySearchResult> relatedMemories)
    {
        var citations = toolOutputs
            .Where(output => output.Success)
            .SelectMany(output => output.Citations)
            .DistinctBy(citation => citation.SourceId)
            .ToList();

        foreach (var memory in relatedMemories)
        {
            citations.Add(new SourceCitation(
                $"memory:{memory.Record.Id}",
                memory.Record.Metadata.GetValueOrDefault("title"),
                memory.Record.Metadata.GetValueOrDefault("url"),
                memory.Record.Content));
        }

        return citations;
    }

    private static double InferConfidence(string summary)
    {
        if (summary.Contains("high confidence", StringComparison.OrdinalIgnoreCase))
        {
            return 0.9d;
        }

        if (summary.Contains("low confidence", StringComparison.OrdinalIgnoreCase))
        {
            return 0.4d;
        }

        return 0.6d;
    }

    private async Task<ExplorationSelection> SelectCitationsForExplorationAsync(
        AgentTask task,
        ToolExecutionResult searchResult,
        CancellationToken cancellationToken)
    {
        if (searchResult.Citations.Count == 0)
        {
            return new ExplorationSelection(Array.Empty<SourceCitation>(), null);
        }

        var prompt = new StringBuilder();
        prompt.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            SystemPrompts.Templates.Researcher.TriageObjectiveLine,
            task.Objective ?? string.Empty));
        prompt.AppendLine(SystemPrompts.Templates.Researcher.TriageGuidance);
        prompt.AppendLine();

        for (int index = 0; index < searchResult.Citations.Count; index++)
        {
            var citation = searchResult.Citations[index];
            string title = string.IsNullOrWhiteSpace(citation.Title) ? "Untitled" : citation.Title;
            prompt.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                SystemPrompts.Templates.Researcher.TriageResultLine,
                index + 1,
                title));
            if (!string.IsNullOrWhiteSpace(citation.Url))
            {
                prompt.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    SystemPrompts.Templates.Researcher.TriageUrlLine,
                    citation.Url));
            }
            if (!string.IsNullOrWhiteSpace(citation.Snippet))
            {
                prompt.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    SystemPrompts.Templates.Researcher.TriageSnippetLine,
                    citation.Snippet));
            }
            prompt.AppendLine();
        }

        var selectionRequest = new OpenAiChatRequest(
            SystemPrompt: SystemPrompts.Researcher.SearchTriage,
            Messages: new[]
            {
                new OpenAiChatMessage("user", prompt.ToString())
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 350);

        string response = await _openAiService.GenerateTextAsync(selectionRequest, cancellationToken).ConfigureAwait(false);
        ParsedSelection parsed = ParseSelectionResponse(response, searchResult.Citations.Count);

        var chosen = new List<SourceCitation>();
        foreach (int selectionIndex in parsed.Indices)
        {
            int zeroBased = selectionIndex - 1;
            if (zeroBased >= 0 && zeroBased < searchResult.Citations.Count)
            {
                var citation = searchResult.Citations[zeroBased];
                if (!chosen.Contains(citation))
                {
                    chosen.Add(citation);
                }
            }
        }

        if (chosen.Count == 0)
        {
            chosen.AddRange(searchResult.Citations.Take(Math.Min(2, searchResult.Citations.Count)));
        }

        return new ExplorationSelection(chosen, parsed.Notes);
    }

    private static ParsedSelection ParseSelectionResponse(string response, int maxCount)
    {
        var indices = new List<int>();
        string? notes = null;

        string? jsonPayload = ExtractJsonSegment(response);
        if (jsonPayload is null)
        {
            return new ParsedSelection(indices, notes);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonPayload);
            if (document.RootElement.TryGetProperty("selected", out JsonElement selectedElement) && selectedElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in selectedElement.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index) && index >= 1 && index <= maxCount)
                    {
                        if (!indices.Contains(index))
                        {
                            indices.Add(index);
                        }
                    }
                }
            }

            if (document.RootElement.TryGetProperty("notes", out JsonElement notesElement) && notesElement.ValueKind == JsonValueKind.String)
            {
                notes = notesElement.GetString();
            }
        }
        catch (JsonException)
        {
            return new ParsedSelection(indices, notes);
        }

        if (indices.Count > 3)
        {
            indices = indices.Take(3).ToList();
        }

        return new ParsedSelection(indices, notes);
    }

    private static string? ExtractJsonSegment(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        string trimmed = response.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        int start = trimmed.IndexOf('{');
        int end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed.Substring(start, end - start + 1);
        }

        return null;
    }

    private sealed record ExplorationSelection(IReadOnlyList<SourceCitation> Citations, string? Notes);

    private sealed record ParsedSelection(IReadOnlyList<int> Indices, string? Notes);
}


