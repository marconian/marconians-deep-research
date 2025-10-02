using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    public ResearcherAgent(
        IAzureOpenAiService openAiService,
        LongTermMemoryManager longTermMemoryManager,
        IEnumerable<ITool> tools,
        string chatDeploymentName,
        ICacheService? cacheService = null,
        ResearchFlowTracker? flowTracker = null,
        ILogger<ResearcherAgent>? logger = null,
        ILogger<ShortTermMemoryManager>? shortTermLogger = null)
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
            logger: _shortTermLogger);

        await shortTermMemory.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await shortTermMemory.AppendAsync("user", task.Objective, cancellationToken).ConfigureAwait(false);

        var toolOutputs = new List<ToolExecutionResult>();
        var errors = new List<string>();
        var flaggedResources = new List<FlaggedResource>();
        var flaggedResourceUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<MemorySearchResult> relatedMemories = await _longTermMemoryManager
            .SearchRelevantAsync(task.ResearchSessionId, task.Objective, 3, cancellationToken)
            .ConfigureAwait(false);

        if (relatedMemories.Count > 0)
        {
            string memorySummary = string.Join('\n', relatedMemories.Select(m => $"- {m.Record.Content}"));
            await shortTermMemory.AppendAsync("memory", memorySummary, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordBranchNote(task.TaskId, $"Loaded {relatedMemories.Count} related memories.");
            _logger.LogDebug("Loaded {Count} related memories into short-term context for task {TaskId}.", relatedMemories.Count, task.TaskId);
        }

        ToolExecutionResult? searchResult = null;
        if (TryGetTool<WebSearchTool>(out var searchTool))
        {
            searchResult = await ExecuteToolAsync(
                task,
                searchTool,
                CreateContext(task, new Dictionary<string, string>
                {
                    ["query"] = task.Objective
                }),
                toolOutputs,
                errors,
                shortTermMemory,
                cancellationToken).ConfigureAwait(false);
        }

        var citationsForFollowUp = searchResult?.Citations?.ToList() ?? new List<SourceCitation>();

        if (searchResult is { Success: true, Citations.Count: > 0 } && TryGetTool<ComputerUseNavigatorTool>(out var navigatorTool))
        {
            ExplorationSelection selection = await SelectCitationsForExplorationAsync(task, searchResult, cancellationToken).ConfigureAwait(false);
            if (selection.Citations.Count > 0)
            {
                citationsForFollowUp = selection.Citations.ToList();
                string selectionMessage = $"Selected {citationsForFollowUp.Count} search result(s) for computer-use exploration.";
                if (!string.IsNullOrWhiteSpace(selection.Notes))
                {
                    selectionMessage += $" Rationale: {selection.Notes}";
                }

                await shortTermMemory.AppendAsync("assistant", selectionMessage, cancellationToken).ConfigureAwait(false);
                _flowTracker?.RecordBranchNote(task.TaskId, selectionMessage);

                    await PersistSelectionAsync(task, selection, cancellationToken).ConfigureAwait(false);

                foreach (var citation in citationsForFollowUp)
                {
                    if (string.IsNullOrWhiteSpace(citation.Url))
                    {
                        continue;
                    }

                    var navParameters = new Dictionary<string, string>
                    {
                        ["url"] = citation.Url,
                        ["objective"] = task.Objective
                    };

                    ToolExecutionResult navResult = await ExecuteToolAsync(
                        task,
                        navigatorTool,
                        CreateContext(task, navParameters),
                        toolOutputs,
                        errors,
                        shortTermMemory,
                        cancellationToken).ConfigureAwait(false);

                    if (navResult.FlaggedResources.Count > 0)
                    {
                        foreach (var resource in navResult.FlaggedResources)
                        {
                            if (string.IsNullOrWhiteSpace(resource.Url))
                            {
                                continue;
                            }

                            if (flaggedResourceUrls.Add(resource.Url))
                            {
                                flaggedResources.Add(resource);
                            }
                        }
                    }
                }
            }
        }

        if (citationsForFollowUp.Count == 0 && searchResult is { Success: true })
        {
            citationsForFollowUp = searchResult.Citations.Take(3).ToList();
        }

        var splitCitations = SplitCitations(citationsForFollowUp);
        citationsForFollowUp = splitCitations.Pages;
        var documentCitations = splitCitations.Documents;

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
            await shortTermMemory.AppendAsync("assistant", flaggedSummary, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordBranchNote(task.TaskId, flaggedSummary);
        }

        var flaggedPageCitations = new List<SourceCitation>();
        var flaggedFileResources = new List<FlaggedResource>();
        var seenFlaggedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in flaggedResources)
        {
            if (string.IsNullOrWhiteSpace(resource.Url) || !seenFlaggedUrls.Add(resource.Url))
            {
                continue;
            }

            if (ShouldTreatAsBinaryResource(resource))
            {
                flaggedFileResources.Add(resource);
                continue;
            }

            flaggedPageCitations.Add(new SourceCitation(
                $"flag:{Guid.NewGuid():N}",
                string.IsNullOrWhiteSpace(resource.Title) ? resource.Url : resource.Title,
                resource.Url,
                resource.Notes ?? resource.MimeType ?? "Flagged during computer-use exploration"));
        }

        if ((citationsForFollowUp.Count > 0 || flaggedPageCitations.Count > 0) && TryGetTool<WebScraperTool>(out var scraperTool))
        {
            var scrapeCandidates = flaggedPageCitations
                .Concat(citationsForFollowUp)
                .Where(citation => !string.IsNullOrWhiteSpace(citation.Url))
                .DistinctBy(static citation => citation.Url, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();

            foreach (var citation in scrapeCandidates)
            {
                if (string.IsNullOrWhiteSpace(citation.Url))
                {
                    continue;
                }

                var scrapeParameters = new Dictionary<string, string>
                {
                    ["url"] = citation.Url
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
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (TryGetTool<FileReaderTool>(out var fileReader))
        {
            var fileReadRequests = new List<Dictionary<string, string>>();
            var requestedFileUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (task.Parameters.Keys.Any(key => key is "fileId" or "url" or "path"))
            {
                fileReadRequests.Add(new Dictionary<string, string>(task.Parameters));
            }

            foreach (var resource in flaggedFileResources.Take(3))
            {
                if (string.IsNullOrWhiteSpace(resource.Url) || !requestedFileUrls.Add(resource.Url))
                {
                    continue;
                }

                var parameters = new Dictionary<string, string>
                {
                    ["url"] = resource.Url
                };

                if (!string.IsNullOrWhiteSpace(resource.MimeType))
                {
                    parameters["contentTypeHint"] = resource.MimeType!;
                }

                if (!string.IsNullOrWhiteSpace(resource.Notes))
                {
                    parameters["note"] = resource.Notes!;
                }

                parameters["origin"] = "computer-use";
                fileReadRequests.Add(parameters);
            }

            foreach (var citation in documentCitations.Take(3))
            {
                if (string.IsNullOrWhiteSpace(citation.Url) || !requestedFileUrls.Add(citation.Url))
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

            foreach (var request in fileReadRequests)
            {
                await ExecuteToolAsync(
                    task,
                    fileReader,
                    CreateContext(task, request),
                    toolOutputs,
                    errors,
                    shortTermMemory,
                    cancellationToken).ConfigureAwait(false);
            }
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
                cancellationToken).ConfigureAwait(false);
        }

        string aggregatedEvidence = BuildEvidenceSummary(toolOutputs, relatedMemories);
        await shortTermMemory.AppendAsync("assistant", aggregatedEvidence, cancellationToken).ConfigureAwait(false);

        var synthesisRequest = new OpenAiChatRequest(
            SystemPrompt: "You are an expert research analyst. Produce concise findings based strictly on provided evidence and prior memories.",
            Messages: new[]
            {
                new OpenAiChatMessage("user", $"Research question: {task.Objective}\n\nEvidence:\n{aggregatedEvidence}\n\nWrite a 3-4 sentence answer summarizing factual findings and note confidence level (High/Medium/Low)."),
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 400);

        string summary = await _openAiService.GenerateTextAsync(synthesisRequest, cancellationToken).ConfigureAwait(false);
        await shortTermMemory.AppendAsync("assistant", summary, cancellationToken).ConfigureAwait(false);

        double confidence = InferConfidence(summary);
        var finding = new ResearchFinding
        {
            Title = task.Objective,
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
            Instruction = task.Objective,
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

        outputs.Add(result);
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

        await shortTermMemory.AppendAsync("tool", $"{tool.Name} => {(result.Success ? "success" : "failure")}", cancellationToken).ConfigureAwait(false);
        if (!result.Success && result.ErrorMessage is not null)
        {
            errors.Add($"{tool.Name}: {result.ErrorMessage}");
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
            builder.AppendLine("Relevant prior memories:");
            foreach (var memory in relatedMemories)
            {
                builder.AppendLine($"- {memory.Record.Content}");
            }
        }

        return builder.Length == 0 ? "No successful evidence collected." : builder.ToString();
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
        prompt.AppendLine($"Research objective: {task.Objective}");
        prompt.AppendLine("Review the search results below and choose up to three that warrant a deeper computer-use browsing session. Favor sources that appear authoritative, comprehensive, or directly aligned with the objective.");
        prompt.AppendLine();

        for (int index = 0; index < searchResult.Citations.Count; index++)
        {
            var citation = searchResult.Citations[index];
            string title = string.IsNullOrWhiteSpace(citation.Title) ? "Untitled" : citation.Title;
            prompt.AppendLine($"{index + 1}. {title}");
            if (!string.IsNullOrWhiteSpace(citation.Url))
            {
                prompt.AppendLine($"   URL: {citation.Url}");
            }
            if (!string.IsNullOrWhiteSpace(citation.Snippet))
            {
                prompt.AppendLine($"   Snippet: {citation.Snippet}");
            }
            prompt.AppendLine();
        }

        var selectionRequest = new OpenAiChatRequest(
            SystemPrompt: "You triage search results for an autonomous researcher. Always reply with strict JSON in the schema {\"selected\":[int],\"notes\":\"string\"}. Indices are 1-based. Include at least one entry when possible.",
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


