using System.Linq;
using System.Text;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Marconian.ResearchAgent.Tools;

namespace Marconian.ResearchAgent.Agents;

public sealed class ResearcherAgent : IAgent
{
    private readonly IAzureOpenAiService _openAiService;
    private readonly LongTermMemoryManager _longTermMemoryManager;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly string _chatDeploymentName;

    public ResearcherAgent(
        IAzureOpenAiService openAiService,
        LongTermMemoryManager longTermMemoryManager,
        IEnumerable<ITool> tools,
        string chatDeploymentName)
    {
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _longTermMemoryManager = longTermMemoryManager ?? throw new ArgumentNullException(nameof(longTermMemoryManager));
        _tools = tools?.ToList() ?? throw new ArgumentNullException(nameof(tools));
        _chatDeploymentName = string.IsNullOrWhiteSpace(chatDeploymentName)
            ? throw new ArgumentException("Chat deployment name must be provided.", nameof(chatDeploymentName))
            : chatDeploymentName;
    }

    public async Task<AgentExecutionResult> ExecuteTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var shortTermMemory = new ShortTermMemoryManager(
            agentId: task.TaskId,
            researchSessionId: task.ResearchSessionId,
            _openAiService,
            _chatDeploymentName);

        await shortTermMemory.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await shortTermMemory.AppendAsync("user", task.Objective, cancellationToken).ConfigureAwait(false);

        var toolOutputs = new List<ToolExecutionResult>();
        var errors = new List<string>();

        IReadOnlyList<MemorySearchResult> relatedMemories = await _longTermMemoryManager
            .SearchRelevantAsync(task.ResearchSessionId, task.Objective, 3, cancellationToken)
            .ConfigureAwait(false);

        if (relatedMemories.Count > 0)
        {
            string memorySummary = string.Join('\n', relatedMemories.Select(m => $"- {m.Record.Content}"));
            await shortTermMemory.AppendAsync("memory", memorySummary, cancellationToken).ConfigureAwait(false);
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

        if (searchResult is { Success: true } && TryGetTool<WebScraperTool>(out var scraperTool))
        {
            foreach (var citation in searchResult.Citations.Take(3))
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

        if (task.Parameters.Keys.Any(key => key is "fileId" or "url" or "path") && TryGetTool<FileReaderTool>(out var fileReader))
        {
            await ExecuteToolAsync(
                task,
                fileReader,
                CreateContext(task, new Dictionary<string, string>(task.Parameters)),
                toolOutputs,
                errors,
                shortTermMemory,
                cancellationToken).ConfigureAwait(false);
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
            MaxOutputTokens: 400,
            Temperature: 0.3f
        );

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

        return new AgentExecutionResult
        {
            Success = true,
            Summary = summary,
            Findings = { finding },
            ToolOutputs = toolOutputs,
            Errors = errors
        };
    }

    private ToolExecutionContext CreateContext(AgentTask task, Dictionary<string, string> parameters)
        => new()
        {
            AgentId = task.TaskId,
            ResearchSessionId = task.ResearchSessionId,
            Instruction = task.Objective,
            Parameters = parameters
        };

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
            }

            if (result.Success || attempt == maxAttempts)
            {
                break;
            }

            TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
        }

        outputs.Add(result);

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
        }

        await shortTermMemory.AppendAsync("tool", $"{tool.Name} => {(result.Success ? "success" : "failure")}", cancellationToken).ConfigureAwait(false);
        if (!result.Success && result.ErrorMessage is not null)
        {
            errors.Add($"{tool.Name}: {result.ErrorMessage}");
        }

        return result;
    }

    private bool TryGetTool<TTool>(out TTool tool)
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
}
