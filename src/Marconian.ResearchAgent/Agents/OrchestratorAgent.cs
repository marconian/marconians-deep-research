using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Research;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Marconian.ResearchAgent.Synthesis;
using Marconian.ResearchAgent.Tools;

namespace Marconian.ResearchAgent.Agents;

public sealed class OrchestratorAgent : IAgent
{
    private readonly IAzureOpenAiService _openAiService;
    private readonly LongTermMemoryManager _longTermMemoryManager;
    private readonly Func<IEnumerable<ITool>> _toolFactory;
    private readonly string _chatDeploymentName;
    private readonly string _reportsDirectory;
    private readonly ResearchAggregator _aggregator = new();
    private readonly MarkdownReportBuilder _reportBuilder = new();

    public OrchestratorState CurrentState { get; private set; } = OrchestratorState.Planning;

    public OrchestratorAgent(
        IAzureOpenAiService openAiService,
        LongTermMemoryManager longTermMemoryManager,
        Func<IEnumerable<ITool>> toolFactory,
        string chatDeploymentName,
        string? reportsDirectory = null)
    {
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _longTermMemoryManager = longTermMemoryManager ?? throw new ArgumentNullException(nameof(longTermMemoryManager));
        _toolFactory = toolFactory ?? throw new ArgumentNullException(nameof(toolFactory));
        _chatDeploymentName = string.IsNullOrWhiteSpace(chatDeploymentName)
            ? throw new ArgumentException("Chat deployment name must be provided.", nameof(chatDeploymentName))
            : chatDeploymentName;
        _reportsDirectory = reportsDirectory is null
            ? Path.Combine(AppContext.BaseDirectory, "reports")
            : Path.GetFullPath(reportsDirectory);

        Directory.CreateDirectory(_reportsDirectory);
    }

    public async Task<AgentExecutionResult> ExecuteTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        ResearchPlan? plan = null;
        List<ResearchBranchResult> branchResults = new();
        ResearchAggregationResult? aggregationResult = null;
        string? synthesis = null;
        string? reportPath = null;

        try
        {
            CurrentState = OrchestratorState.Planning;
            plan = await GeneratePlanAsync(task, cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.ExecutingResearchBranches;
            branchResults = await ExecuteBranchesAsync(task, plan, cancellationToken).ConfigureAwait(false);

            aggregationResult = _aggregator.Aggregate(branchResults);

            CurrentState = OrchestratorState.SynthesizingResults;
            synthesis = await SynthesizeAsync(task, plan, aggregationResult, cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.GeneratingReport;
            reportPath = await GenerateReportAsync(task, aggregationResult, synthesis, cancellationToken).ConfigureAwait(false);

            var finalFinding = new ResearchFinding
            {
                Title = task.Objective,
                Content = synthesis,
                Citations = aggregationResult.UniqueCitations.ToList(),
                Confidence = aggregationResult.Findings.Any()
                    ? Math.Clamp(aggregationResult.Findings.Average(f => f.Confidence), 0d, 1d)
                    : 0.5d
            };

            await _longTermMemoryManager.StoreFindingAsync(
                task.ResearchSessionId,
                finalFinding,
                memoryType: "final_report_finding",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await _longTermMemoryManager.StoreDocumentAsync(
                task.ResearchSessionId,
                synthesis,
                "final_report",
                finalFinding.Citations,
                new Dictionary<string, string>
                {
                    ["reportPath"] = reportPath ?? string.Empty
                },
                cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.Completed;

            return new AgentExecutionResult
            {
                Success = true,
                Summary = synthesis,
                Findings = new List<ResearchFinding> { finalFinding },
                ToolOutputs = branchResults.SelectMany(result => result.ToolOutputs).ToList(),
                Errors = branchResults
                    .SelectMany(result => result.Notes
                        .Concat(result.ToolOutputs.Where(o => !o.Success && !string.IsNullOrWhiteSpace(o.ErrorMessage))
                        .Select(o => $"{o.ToolName}: {o.ErrorMessage}")))
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .ToList(),
                Metadata = new Dictionary<string, string>
                {
                    ["reportPath"] = reportPath ?? string.Empty,
                    ["researchSessionId"] = task.ResearchSessionId
                }
            };
        }
        catch (Exception ex)
        {
            CurrentState = OrchestratorState.Failed;
            return new AgentExecutionResult
            {
                Success = false,
                Summary = $"Orchestration failed: {ex.Message}",
                Findings = new List<ResearchFinding>(),
                Errors = new List<string> { ex.ToString() },
                Metadata = new Dictionary<string, string>
                {
                    ["researchSessionId"] = task.ResearchSessionId
                }
            };
        }
    }

    private async Task<ResearchPlan> GeneratePlanAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var request = new OpenAiChatRequest(
            SystemPrompt: "You are a strategist decomposing complex research projects into parallelizable branches.",
            Messages: new[]
            {
                new OpenAiChatMessage("user", $"Break the following research objective into 3-5 focused sub-questions suitable for parallel investigation. Respond with a numbered list only.\nObjective: {task.Objective}")
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 300,
            Temperature: 0.3f
        );

        string response = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        var branchQuestions = ParseQuestions(response);
        if (branchQuestions.Count == 0)
        {
            branchQuestions.Add(task.Objective);
        }

        return new ResearchPlan
        {
            ResearchSessionId = task.ResearchSessionId,
            RootQuestion = task.Objective,
            Branches = branchQuestions.Select(question => new ResearchBranchPlan
            {
                Question = question
            }).ToList()
        };
    }

    private async Task<List<ResearchBranchResult>> ExecuteBranchesAsync(AgentTask task, ResearchPlan plan, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<ResearchBranchResult>();

        var branchTasks = plan.Branches.Select(branch => Task.Run(async () =>
        {
            branch.Status = ResearchBranchStatus.InProgress;
            var branchTask = new AgentTask
            {
                TaskId = branch.BranchId,
                ResearchSessionId = task.ResearchSessionId,
                Objective = branch.Question,
                Parameters = new Dictionary<string, string>(task.Parameters),
                ContextHints = new List<string>(task.ContextHints)
            };

            var researcher = new ResearcherAgent(_openAiService, _longTermMemoryManager, _toolFactory(), _chatDeploymentName);
            AgentExecutionResult result = await researcher.ExecuteTaskAsync(branchTask, cancellationToken).ConfigureAwait(false);

            branch.Status = result.Success ? ResearchBranchStatus.Completed : ResearchBranchStatus.Failed;

            results.Add(new ResearchBranchResult
            {
                BranchId = branch.BranchId,
                Finding = result.Findings.FirstOrDefault() ?? new ResearchFinding
                {
                    Title = branch.Question,
                    Content = result.Summary,
                    Citations = new List<SourceCitation>(),
                    Confidence = 0.3d
                },
                ToolOutputs = result.ToolOutputs.ToList(),
                Notes = result.Errors
            });
        }, cancellationToken)).ToList();

        await Task.WhenAll(branchTasks).ConfigureAwait(false);
        return results.ToList();
    }

    private async Task<string> SynthesizeAsync(
        AgentTask rootTask,
        ResearchPlan plan,
        ResearchAggregationResult aggregationResult,
        CancellationToken cancellationToken)
    {
        var evidenceBuilder = new StringBuilder();
        foreach (var finding in aggregationResult.Findings)
        {
            evidenceBuilder.AppendLine($"Finding: {finding.Title}");
            evidenceBuilder.AppendLine(finding.Content);
            evidenceBuilder.AppendLine();
        }

        var request = new OpenAiChatRequest(
            SystemPrompt: "You synthesize multiple research findings into a cohesive narrative with citations.",
            Messages: new[]
            {
                new OpenAiChatMessage("user", $"Primary question: {rootTask.Objective}\n\nFindings:\n{evidenceBuilder}\n\nWrite a cohesive synthesis referencing findings as [Source #]. Conclude with overall confidence."),
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 600,
            Temperature: 0.3f
        );

        return await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GenerateReportAsync(
        AgentTask rootTask,
        ResearchAggregationResult aggregationResult,
        string synthesis,
        CancellationToken cancellationToken)
    {
        string markdown = _reportBuilder.Build(rootTask.Objective, synthesis, aggregationResult.Findings);
        string fileName = $"report_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.md";
        string reportPath = Path.Combine(_reportsDirectory, fileName);
        await File.WriteAllTextAsync(reportPath, markdown, cancellationToken).ConfigureAwait(false);
        return reportPath;
    }

    private static List<string> ParseQuestions(string response)
    {
        var questions = new List<string>();
        foreach (var line in response.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            trimmed = trimmed.TrimStart('-', '*');
            int dotIndex = trimmed.IndexOf('.');
            if (dotIndex > -1 && dotIndex < 4)
            {
                trimmed = trimmed[(dotIndex + 1)..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                questions.Add(trimmed);
            }
        }

        return questions;
    }
}
