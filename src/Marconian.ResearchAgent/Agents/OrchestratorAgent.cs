using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Research;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Marconian.ResearchAgent.Synthesis;
using Marconian.ResearchAgent.Tools;
using Marconian.ResearchAgent.Tracking;
using Marconian.ResearchAgent.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<OrchestratorAgent> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICacheService? _cacheService;
    private readonly ResearchFlowTracker? _flowTracker;
    private readonly int _maxRevisionPasses;
    private readonly JsonSerializerOptions _revisionOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public OrchestratorState CurrentState { get; private set; } = OrchestratorState.Planning;

    public OrchestratorAgent(
        IAzureOpenAiService openAiService,
        LongTermMemoryManager longTermMemoryManager,
        Func<IEnumerable<ITool>> toolFactory,
        string chatDeploymentName,
        ILogger<OrchestratorAgent>? logger = null,
        ILoggerFactory? loggerFactory = null,
        ICacheService? cacheService = null,
        ResearchFlowTracker? flowTracker = null,
        string? reportsDirectory = null,
        int maxReportRevisionPasses = 2)
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

        _logger = logger ?? NullLogger<OrchestratorAgent>.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _cacheService = cacheService;
        _flowTracker = flowTracker;
        _maxRevisionPasses = Math.Max(0, maxReportRevisionPasses);
    }

    public async Task<AgentExecutionResult> ExecuteTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        string diagramPath = Path.Combine(_reportsDirectory, $"flow_{task.ResearchSessionId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.md");
        _flowTracker?.ConfigureSession(task.ResearchSessionId, task.Objective ?? string.Empty, diagramPath);

        ResearchPlan? plan = null;
        List<ResearchBranchResult> branchResults = new();
        ResearchAggregationResult? aggregationResult = null;
        string? synthesis = null;
        string? reportPath = null;
        int revisionsApplied = 0;

        try
        {
            _logger.LogInformation("Starting orchestrator for session {SessionId}: {Objective}.", task.ResearchSessionId, task.Objective);
            await _longTermMemoryManager.UpsertSessionStateAsync(task.ResearchSessionId, "planning", task.Objective, cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.Planning;
            plan = await GeneratePlanAsync(task, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordPlan(plan);
            _logger.LogInformation("Generated research plan with {BranchCount} branches.", plan.Branches.Count);
            await _longTermMemoryManager.UpsertSessionStateAsync(
                task.ResearchSessionId,
                "plan_ready",
                $"Branches: {plan.Branches.Count}",
                cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.ExecutingResearchBranches;
            branchResults = await ExecuteBranchesAsync(task, plan, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Completed branch execution with {SuccessCount} successful findings out of {Total} branches.",
                branchResults.Count(result => result.Finding is not null),
                branchResults.Count);
            await _longTermMemoryManager.UpsertSessionStateAsync(
                task.ResearchSessionId,
                "branches_completed",
                $"Branches: {branchResults.Count}",
                cancellationToken).ConfigureAwait(false);

            aggregationResult = _aggregator.Aggregate(branchResults);
            _flowTracker?.RecordAggregation(aggregationResult.Findings.Count);

            CurrentState = OrchestratorState.SynthesizingResults;
            synthesis = await SynthesizeAsync(task, plan, aggregationResult, cancellationToken).ConfigureAwait(false);
            await _longTermMemoryManager.UpsertSessionStateAsync(
                task.ResearchSessionId,
                "synthesizing",
                $"Findings: {aggregationResult.Findings.Count}",
                cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.GeneratingReport;
            reportPath = await GenerateReportAsync(task, aggregationResult, synthesis, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordReportDraft(reportPath);
            _logger.LogInformation("Initial report generated at {ReportPath}.", reportPath);

            revisionsApplied = await ReviseReportAsync(task, aggregationResult, reportPath, cancellationToken).ConfigureAwait(false);
            if (revisionsApplied > 0)
            {
                _logger.LogInformation("Applied {RevisionCount} revision passes to report.", revisionsApplied);
            }

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

            if (string.IsNullOrWhiteSpace(reportPath))
            {
                throw new InvalidOperationException("Report generation completed without producing a file path.");
            }

            string finalReportContent = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);

            await _longTermMemoryManager.StoreDocumentAsync(
                task.ResearchSessionId,
                finalReportContent,
                "final_report",
                finalFinding.Citations,
                new Dictionary<string, string>
                {
                    ["reportPath"] = reportPath ?? string.Empty,
                    ["revisionsApplied"] = revisionsApplied.ToString(CultureInfo.InvariantCulture)
                },
                cancellationToken).ConfigureAwait(false);

            await _longTermMemoryManager.UpsertSessionStateAsync(
                task.ResearchSessionId,
                "completed",
                $"Report revisions: {revisionsApplied}",
                cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.Completed;

            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                _flowTracker?.RecordArtifacts(reportPath);
            }
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
                    ["researchSessionId"] = task.ResearchSessionId,
                    ["revisionPasses"] = revisionsApplied.ToString(CultureInfo.InvariantCulture)
                }
            };
        }
        catch (Exception ex)
        {
            CurrentState = OrchestratorState.Failed;
            _logger.LogError(ex, "Orchestrator failed for session {SessionId}.", task.ResearchSessionId);
            await _longTermMemoryManager.UpsertSessionStateAsync(
                task.ResearchSessionId,
                "failed",
                ex.Message,
                cancellationToken).ConfigureAwait(false);

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

        finally
        {
            if (_flowTracker is not null)
            {
                await _flowTracker.SaveAsync(cancellationToken).ConfigureAwait(false);
            }
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
            MaxOutputTokens: 300);

        _logger.LogDebug("Requesting research plan for objective '{Objective}'.", task.Objective);
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
        var branchTasks = new List<Task>();

        foreach (var branch in plan.Branches)
        {
            branchTasks.Add(Task.Run(async () =>
            {
                _flowTracker?.RecordBranchStarted(branch.BranchId);
                await _longTermMemoryManager.UpsertBranchStateAsync(
                    task.ResearchSessionId,
                    branch.BranchId,
                    branch.Question,
                    "in_progress",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                branch.Status = ResearchBranchStatus.InProgress;
                var branchTask = new AgentTask
                {
                    TaskId = branch.BranchId,
                    ResearchSessionId = task.ResearchSessionId,
                    Objective = branch.Question,
                    Parameters = new Dictionary<string, string>(task.Parameters),
                    ContextHints = new List<string>(task.ContextHints)
                };

                try
                {
                    var researcherLogger = _loggerFactory.CreateLogger<ResearcherAgent>();
                    var shortTermLogger = _loggerFactory.CreateLogger<ShortTermMemoryManager>();
                    var researcher = new ResearcherAgent(
                        _openAiService,
                        _longTermMemoryManager,
                        _toolFactory(),
                        _chatDeploymentName,
                        _cacheService,
                        _flowTracker,
                        researcherLogger,
                        shortTermLogger);

                    AgentExecutionResult result = await researcher.ExecuteTaskAsync(branchTask, cancellationToken).ConfigureAwait(false);
                    _flowTracker?.RecordBranchCompleted(branch.BranchId, result.Success, result.Summary);

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

                    await _longTermMemoryManager.UpsertBranchStateAsync(
                        task.ResearchSessionId,
                        branch.BranchId,
                        branch.Question,
                        branch.Status.ToString().ToLowerInvariant(),
                        result.Summary,
                        result.ToolOutputs.Select(output => output.ToolName).ToList(),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    branch.Status = ResearchBranchStatus.Failed;
                    _logger.LogError(ex, "Branch {BranchId} failed.", branch.BranchId);
                    _flowTracker?.RecordBranchNote(branch.BranchId, $"Exception: {ex.Message}");
                    _flowTracker?.RecordBranchCompleted(branch.BranchId, false, ex.Message);

                    results.Add(new ResearchBranchResult
                    {
                        BranchId = branch.BranchId,
                        Finding = new ResearchFinding
                        {
                            Title = branch.Question,
                            Content = string.Empty,
                            Citations = new List<SourceCitation>(),
                            Confidence = 0.2d
                        },
                        ToolOutputs = new List<ToolExecutionResult>(),
                        Notes = new List<string> { ex.Message }
                    });

                    await _longTermMemoryManager.UpsertBranchStateAsync(
                        task.ResearchSessionId,
                        branch.BranchId,
                        branch.Question,
                        "failed",
                        ex.Message,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken));
        }

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
            MaxOutputTokens: 600);

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

    private async Task<int> ReviseReportAsync(
        AgentTask rootTask,
        ResearchAggregationResult aggregationResult,
        string reportPath,
        CancellationToken cancellationToken)
    {
        if (_maxRevisionPasses <= 0)
        {
            return 0;
        }

        int appliedPasses = 0;
        for (int pass = 1; pass <= _maxRevisionPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
            var editor = MarkdownReportEditor.FromContent(content);
            string enumerated = editor.EnumerateLines();

            var promptBuilder = new StringBuilder();
            promptBuilder.AppendLine($"Objective: {rootTask.Objective}");
            promptBuilder.AppendLine("Existing report lines:");
            promptBuilder.AppendLine(enumerated);
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("Suggest up to three targeted edits to improve clarity, add missing citations, or fix structural issues. Return a JSON array of objects with properties 'action' ('replace' | 'insert_before' | 'insert_after'), 'line' (1-based) and 'content'. If no changes are needed, return an empty JSON array.");

            var request = new OpenAiChatRequest(
                SystemPrompt: "You are an editor that refines Markdown research reports with precise, line-targeted edits.",
                Messages: new[]
                {
                    new OpenAiChatMessage("user", promptBuilder.ToString())
                },
                DeploymentName: _chatDeploymentName,
                MaxOutputTokens: 400);

            string response = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
            if (!TryParseReportInstructions(response, out var instructions) || instructions.Count == 0)
            {
                _logger.LogDebug("Report revision pass {Pass} produced no actionable instructions.", pass);
                break;
            }

            bool modified = ApplyReportInstructions(editor, instructions);
            if (!modified)
            {
                _logger.LogDebug("Report revision pass {Pass} instructions could not be applied.", pass);
                _flowTracker?.RecordReportRevision(pass, applied: false);
                break;
            }

            editor.Save(reportPath);
            _flowTracker?.RecordReportRevision(pass, applied: true);
            appliedPasses = pass;
        }

        return appliedPasses;
    }

    private bool TryParseReportInstructions(string response, out List<ReportEditInstruction> instructions)
    {
        instructions = new List<ReportEditInstruction>();
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        int start = response.IndexOf('[');
        int end = response.LastIndexOf(']');
        if (start < 0 || end < start)
        {
            return false;
        }

        string json = response[start..(end + 1)];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<ReportEditInstruction>>(json, _revisionOptions);
            if (parsed is null)
            {
                return false;
            }

            instructions = parsed
                .Where(item => !string.IsNullOrWhiteSpace(item.Action))
                .Take(5)
                .ToList();
            return instructions.Count > 0;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse report edit instructions. Raw response: {Response}", response);
            return false;
        }
    }

    private static bool ApplyReportInstructions(MarkdownReportEditor editor, IReadOnlyList<ReportEditInstruction> instructions)
    {
        bool modified = false;
        foreach (var instruction in instructions)
        {
            string action = instruction.Action?.Trim().ToLowerInvariant() ?? string.Empty;
            int line = instruction.Line <= 0 ? 1 : instruction.Line;
            string content = instruction.Content ?? string.Empty;

            switch (action)
            {
                case "replace":
                    editor.ReplaceLine(line, content);
                    modified = true;
                    break;
                case "insert_after":
                    editor.InsertAfter(line, content);
                    modified = true;
                    break;
                case "insert_before":
                    editor.InsertBefore(line, content);
                    modified = true;
                    break;
            }
        }

        return modified;
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

    private sealed class ReportEditInstruction
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("line")]
        public int Line { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}
















