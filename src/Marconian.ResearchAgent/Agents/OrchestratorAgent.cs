using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Marconian.ResearchAgent.Configuration;
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
    private readonly OrchestratorOptions _options;
    private readonly ResearcherOptions _researcherOptions;
    private readonly ShortTermMemoryOptions _shortTermOptions;
    private readonly int _maxRevisionPasses;
    private readonly JsonSerializerOptions _revisionOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly OpenAiChatJsonSchemaFormat OutlineSchemaFormat = BuildOutlineSchemaFormat();
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
        OrchestratorOptions? orchestratorOptions = null,
        ResearcherOptions? researcherOptions = null,
        ShortTermMemoryOptions? shortTermMemoryOptions = null)
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
        _options = SanitizeOrchestratorOptions(orchestratorOptions);
        _researcherOptions = SanitizeResearcherOptions(researcherOptions);
        _shortTermOptions = shortTermMemoryOptions is null
            ? new ShortTermMemoryOptions()
            : new ShortTermMemoryOptions
            {
                MaxEntries = shortTermMemoryOptions.MaxEntries,
                SummaryBatchSize = shortTermMemoryOptions.SummaryBatchSize,
                CacheTtlHours = shortTermMemoryOptions.CacheTtlHours
            };
        _maxRevisionPasses = Math.Max(0, _options.MaxReportRevisionPasses);
    }

    private static OrchestratorOptions SanitizeOrchestratorOptions(OrchestratorOptions? options)
    {
        var resolved = options is null
            ? new OrchestratorOptions()
            : new OrchestratorOptions
            {
                MaxResearchPasses = options.MaxResearchPasses,
                MaxFollowupQuestions = options.MaxFollowupQuestions,
                MaxSectionEvidenceCharacters = options.MaxSectionEvidenceCharacters,
                MaxReportRevisionPasses = options.MaxReportRevisionPasses
            };

        resolved.MaxResearchPasses = Math.Max(1, resolved.MaxResearchPasses);
        resolved.MaxFollowupQuestions = Math.Max(0, resolved.MaxFollowupQuestions);
        resolved.MaxSectionEvidenceCharacters = Math.Max(500, resolved.MaxSectionEvidenceCharacters);
        resolved.MaxReportRevisionPasses = Math.Max(0, resolved.MaxReportRevisionPasses);
        return resolved;
    }

    private static ResearcherOptions SanitizeResearcherOptions(ResearcherOptions? options)
    {
        var resolved = options is null
            ? new ResearcherOptions()
            : new ResearcherOptions
            {
                MaxSearchIterations = options.MaxSearchIterations,
                MaxContinuationQueries = options.MaxContinuationQueries,
                RelatedMemoryTake = options.RelatedMemoryTake
            };

        resolved.MaxSearchIterations = Math.Max(1, resolved.MaxSearchIterations);
        resolved.MaxContinuationQueries = Math.Max(0, resolved.MaxContinuationQueries);
        resolved.RelatedMemoryTake = Math.Max(0, resolved.RelatedMemoryTake);
        return resolved;
    }

    public async Task<AgentExecutionResult> ExecuteTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        bool reportOnlyMode = task.Parameters.TryGetValue("reportOnly", out var reportOnlyValue) && IsAffirmative(reportOnlyValue);
        string diagramPath = Path.Combine(_reportsDirectory, $"flow_{task.ResearchSessionId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.md");
        _flowTracker?.ConfigureSession(task.ResearchSessionId, task.Objective ?? string.Empty, diagramPath);

        ResearchPlan? plan = null;
        var branchResults = new List<ResearchBranchResult>();
        ResearchAggregationResult? aggregationResult = null;
        string? synthesis = null;
        ReportOutline? outline = null;
        var sectionDrafts = new List<ReportSectionDraft>();
        string? reportPath = null;
        int revisionsApplied = 0;

        try
        {
            _logger.LogInformation("Starting orchestrator for session {SessionId}: {Objective}.", task.ResearchSessionId, task.Objective);
            await _longTermMemoryManager.UpsertSessionStateAsync(task.ResearchSessionId, "planning", task.Objective, cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.Planning;
            if (reportOnlyMode)
            {
                var restoredFindings = await _longTermMemoryManager.RestoreFindingsAsync(
                        task.ResearchSessionId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (restoredFindings.Count == 0)
                {
                    throw new InvalidOperationException("No stored findings are available for this session. Run a full research cycle before regenerating the report.");
                }

                plan = new ResearchPlan
                {
                    ResearchSessionId = task.ResearchSessionId,
                    RootQuestion = task.Objective ?? string.Empty
                };

                var restoredPairs = new List<(ResearchBranchPlan Branch, ResearchFinding Finding)>(restoredFindings.Count);
                foreach (var finding in restoredFindings)
                {
                    string question = string.IsNullOrWhiteSpace(finding.Title)
                        ? "Restored finding"
                        : finding.Title;

                    var branchPlan = new ResearchBranchPlan
                    {
                        Question = question,
                        Status = ResearchBranchStatus.Completed
                    };

                    plan.Branches.Add(branchPlan);
                    restoredPairs.Add((branchPlan, finding));
                }

                _flowTracker?.RecordPlan(plan);

                foreach (var (branch, finding) in restoredPairs)
                {
                    branchResults.Add(new ResearchBranchResult
                    {
                        BranchId = branch.BranchId,
                        Finding = finding,
                        ToolOutputs = new List<ToolExecutionResult>(),
                        Notes = new List<string>
                        {
                            "Restored from stored findings for report regeneration."
                        }
                    });

                    _flowTracker?.RecordBranchCompleted(branch.BranchId, true, "Restored from stored findings.");
                    await _longTermMemoryManager.UpsertBranchStateAsync(
                        task.ResearchSessionId,
                        branch.BranchId,
                        branch.Question,
                        "restored",
                        summary: finding.Title,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation("Restored {FindingCount} stored finding(s) for session {SessionId}.", branchResults.Count, task.ResearchSessionId);
                await _longTermMemoryManager.UpsertSessionStateAsync(
                    task.ResearchSessionId,
                    "report_regeneration_loaded",
                    $"Restored findings: {branchResults.Count}",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                plan = await GeneratePlanAsync(task, cancellationToken).ConfigureAwait(false);
                if (plan is null)
                {
                    throw new InvalidOperationException("Failed to generate a research plan.");
                }

                _flowTracker?.RecordPlan(plan);
                _logger.LogInformation("Generated research plan with {BranchCount} branches.", plan.Branches.Count);
                await _longTermMemoryManager.UpsertSessionStateAsync(
                    task.ResearchSessionId,
                    "plan_ready",
                    $"Branches: {plan.Branches.Count}",
                    cancellationToken).ConfigureAwait(false);
            }

            CurrentState = OrchestratorState.ExecutingResearchBranches;
            if (!reportOnlyMode)
            {
                aggregationResult = await RunResearchWorkflowAsync(task, plan!, branchResults, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Completed research workflow with {FindingCount} aggregated findings across {BranchCount} branches.",
                    aggregationResult.Findings.Count,
                    branchResults.Count);
                await _longTermMemoryManager.UpsertSessionStateAsync(
                    task.ResearchSessionId,
                    "branches_completed",
                    $"Branches: {branchResults.Count}",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                aggregationResult = _aggregator.Aggregate(branchResults);
                _flowTracker?.RecordAggregation(aggregationResult.Findings.Count);
                _logger.LogInformation("Aggregated {FindingCount} stored finding(s) for report regeneration.", aggregationResult.Findings.Count);
                await _longTermMemoryManager.UpsertSessionStateAsync(
                    task.ResearchSessionId,
                    "report_regeneration_aggregated",
                    $"Findings: {aggregationResult.Findings.Count}",
                    cancellationToken).ConfigureAwait(false);
            }

            CurrentState = OrchestratorState.SynthesizingResults;
            synthesis = await SynthesizeAsync(task, plan, aggregationResult, cancellationToken).ConfigureAwait(false);
            await _longTermMemoryManager.UpsertSessionStateAsync(
                task.ResearchSessionId,
                "synthesizing",
                $"Findings: {aggregationResult.Findings.Count}",
                cancellationToken).ConfigureAwait(false);

            CurrentState = OrchestratorState.GeneratingReport;
            outline = await GenerateOutlineAsync(task, aggregationResult, synthesis, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordOutline(outline);

            sectionDrafts = await DraftReportSectionsAsync(task, outline, aggregationResult, synthesis, cancellationToken).ConfigureAwait(false);
            foreach (var draft in sectionDrafts)
            {
                _flowTracker?.RecordSectionDraft(draft);
            }

            reportPath = await GenerateReportAsync(task, aggregationResult, synthesis, outline, sectionDrafts, cancellationToken).ConfigureAwait(false);
            _flowTracker?.RecordReportDraft(reportPath);
            _logger.LogInformation("Initial report generated at {ReportPath}.", reportPath);

            revisionsApplied = await ReviseReportAsync(task, aggregationResult, reportPath, cancellationToken).ConfigureAwait(false);
            if (revisionsApplied > 0)
            {
                _logger.LogInformation("Applied {RevisionCount} revision passes to report.", revisionsApplied);
            }

            var finalFinding = new ResearchFinding
            {
                Title = task.Objective ?? string.Empty,
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
                    ["outlineId"] = outline?.OutlineId ?? string.Empty,
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

    private async Task<ResearchAggregationResult> RunResearchWorkflowAsync(
        AgentTask task,
        ResearchPlan plan,
        List<ResearchBranchResult> accumulatedResults,
        CancellationToken cancellationToken)
    {
        ResearchAggregationResult aggregation = _aggregator.Aggregate(accumulatedResults);

    for (int pass = 0; pass < _options.MaxResearchPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pendingBranches = plan.Branches.Where(static branch => branch.Status == ResearchBranchStatus.Pending).ToList();
            if (pendingBranches.Count > 0)
            {
                IReadOnlyList<ResearchBranchResult> newResults = await ExecuteBranchesAsync(task, plan, cancellationToken).ConfigureAwait(false);
                if (newResults.Count > 0)
                {
                    accumulatedResults.AddRange(newResults);
                }
            }

            aggregation = _aggregator.Aggregate(accumulatedResults);
            _flowTracker?.RecordAggregation(aggregation.Findings.Count);

            ResearchContinuationDecision decision = await EvaluateResearchContinuationAsync(
                    task,
                    plan,
                    aggregation,
                    pass + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!decision.ShouldContinue)
            {
                break;
            }

            var newQuestions = decision.NewQuestions
                .Where(static question => !string.IsNullOrWhiteSpace(question))
                .Select(static question => question.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(question => !plan.Branches.Any(branch => string.Equals(branch.Question, question, StringComparison.OrdinalIgnoreCase)))
                .Take(_options.MaxFollowupQuestions)
                .ToList();

            if (newQuestions.Count == 0)
            {
                _logger.LogDebug("No additional research questions proposed for session {SessionId} after pass {Pass}.", task.ResearchSessionId, pass + 1);
                break;
            }

            foreach (string question in newQuestions)
            {
                plan.Branches.Add(new ResearchBranchPlan
                {
                    Question = question
                });
            }

            _flowTracker?.RecordPlan(plan);
            await _longTermMemoryManager.UpsertSessionStateAsync(
                task.ResearchSessionId,
                "plan_extended",
                string.Join(" | ", newQuestions),
                cancellationToken).ConfigureAwait(false);
        }

        return aggregation;
    }

    private async Task<ResearchContinuationDecision> EvaluateResearchContinuationAsync(
        AgentTask task,
        ResearchPlan plan,
        ResearchAggregationResult aggregation,
        int passNumber,
        CancellationToken cancellationToken)
    {
    if (passNumber >= _options.MaxResearchPasses)
        {
            return new ResearchContinuationDecision(false, Array.Empty<string>());
        }

        var planSummaryBuilder = new StringBuilder();
        foreach (var branch in plan.Branches)
        {
            planSummaryBuilder.Append("- ");
            planSummaryBuilder.Append(branch.Question);
            planSummaryBuilder.Append(" (status: ");
            planSummaryBuilder.Append(branch.Status);
            planSummaryBuilder.AppendLine(")");
        }

        var findingBuilder = new StringBuilder();
        foreach (var finding in aggregation.Findings.Take(6))
        {
            findingBuilder.AppendLine($"Finding: {finding.Title}");
            if (!string.IsNullOrWhiteSpace(finding.Content))
            {
                string content = finding.Content.Length > _options.MaxSectionEvidenceCharacters
                    ? finding.Content[.._options.MaxSectionEvidenceCharacters]
                    : finding.Content;
                findingBuilder.AppendLine(content);
            }

            if (finding.Citations.Count > 0)
            {
                findingBuilder.AppendLine($"Citations: {string.Join(", ", finding.Citations.Select(c => c.SourceId))}");
            }

            findingBuilder.AppendLine();
        }

        if (findingBuilder.Length == 0)
        {
            findingBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.ContinuationNoFindings);
        }

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            SystemPrompts.Templates.Orchestrator.ContinuationObjectiveLine,
            task.Objective ?? string.Empty));
        promptBuilder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            SystemPrompts.Templates.Orchestrator.ContinuationPassLine,
            passNumber,
            _options.MaxResearchPasses));
        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.ContinuationBranchHeader);
        promptBuilder.AppendLine(planSummaryBuilder.ToString());
        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.ContinuationFindingsHeader);
        promptBuilder.AppendLine(findingBuilder.ToString());
        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.ContinuationDecisionInstruction);
        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.ContinuationResponseInstruction);

        var request = new OpenAiChatRequest(
            SystemPrompt: SystemPrompts.Orchestrator.ContinuationDirector,
            Messages: new[]
            {
                new OpenAiChatMessage("user", promptBuilder.ToString())
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 350);

        string response = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        if (!TryParseContinuationDecision(response, out var decision))
        {
            bool defaultContinue = plan.Branches.Any(static branch => branch.Status == ResearchBranchStatus.Pending)
                && aggregation.Findings.Count < plan.Branches.Count;

            return new ResearchContinuationDecision(defaultContinue, Array.Empty<string>());
        }

        return decision;
    }

    private async Task<ResearchPlan> GeneratePlanAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var request = new OpenAiChatRequest(
            SystemPrompt: SystemPrompts.Orchestrator.PlanningStrategist,
            Messages: new[]
            {
                new OpenAiChatMessage(
                    "user",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        SystemPrompts.Templates.Orchestrator.PlanningRequest,
                        task.Objective ?? string.Empty))
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 300);

        _logger.LogDebug("Requesting research plan for objective '{Objective}'.", task.Objective);
        string response = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        var branchQuestions = ParseQuestions(response);
        if (branchQuestions.Count == 0)
        {
            branchQuestions.Add(task.Objective ?? string.Empty);
        }

        return new ResearchPlan
        {
            ResearchSessionId = task.ResearchSessionId,
            RootQuestion = task.Objective ?? string.Empty,
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

        foreach (var branch in plan.Branches.Where(static b => b.Status == ResearchBranchStatus.Pending).ToList())
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
                        shortTermLogger,
                        options: _researcherOptions,
                        shortTermOptions: _shortTermOptions);

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
            SystemPrompt: SystemPrompts.Orchestrator.SynthesisAuthor,
            Messages: new[]
            {
                new OpenAiChatMessage(
                    "user",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        SystemPrompts.Templates.Orchestrator.SynthesisRequest,
                        rootTask.Objective ?? string.Empty,
                        evidenceBuilder.ToString()))
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 600);

        return await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReportOutline> GenerateOutlineAsync(
        AgentTask rootTask,
        ResearchAggregationResult aggregationResult,
        string synthesis,
        CancellationToken cancellationToken)
    {
        var findings = aggregationResult.Findings ?? Array.Empty<ResearchFinding>();
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            SystemPrompts.Templates.Common.ObjectiveLine,
            rootTask.Objective ?? string.Empty));
        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.SynthesisOverviewHeader);
        promptBuilder.AppendLine(string.IsNullOrWhiteSpace(synthesis)
            ? SystemPrompts.Templates.Orchestrator.SynthesisNoSynthesis
            : synthesis.Trim());
        promptBuilder.AppendLine();
        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.SynthesisFindingsHeader);

        foreach (var finding in findings.Take(10))
        {
            string content = finding.Content.Length > _options.MaxSectionEvidenceCharacters
                ? finding.Content[.._options.MaxSectionEvidenceCharacters]
                : finding.Content;
            promptBuilder.AppendLine($"- Id: {finding.Id}");
            promptBuilder.AppendLine($"  Title: {finding.Title}");
            promptBuilder.AppendLine($"  Confidence: {finding.Confidence:F2}");
            promptBuilder.AppendLine($"  Summary: {content}");
            promptBuilder.AppendLine();
        }

        if (findings.Count == 0)
        {
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.SynthesisNoFindings);
        }

        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineInstruction);

        var request = new OpenAiChatRequest(
            SystemPrompt: SystemPrompts.Orchestrator.OutlineEditor,
            Messages: new[]
            {
                new OpenAiChatMessage("user", promptBuilder.ToString())
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 500,
            JsonSchemaFormat: OutlineSchemaFormat);

        string response = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        if (!TryParseOutline(response, rootTask.Objective ?? string.Empty, findings, out var outline))
        {
            outline = BuildFallbackOutline(rootTask.Objective ?? string.Empty, findings);
        }

        return outline;
    }

    private async Task<List<ReportSectionDraft>> DraftReportSectionsAsync(
        AgentTask rootTask,
        ReportOutline outline,
        ResearchAggregationResult aggregationResult,
        string synthesis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var drafts = new List<ReportSectionDraft>();
        var findingMap = aggregationResult.Findings.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var sectionLookup = outline.Sections.ToDictionary(section => section.SectionId, StringComparer.OrdinalIgnoreCase);
        var draftedSectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sectionContexts = BuildSectionContexts(outline.Layout, sectionLookup);
        var priorDrafts = new List<ReportSectionDraft>();

        IEnumerable<string> EnumerateLayoutSections(IEnumerable<ReportLayoutNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.SectionId))
                {
                    yield return node.SectionId!;
                }

                foreach (var childId in EnumerateLayoutSections(node.Children))
                {
                    yield return childId;
                }
            }
        }

        foreach (string sectionId in EnumerateLayoutSections(outline.Layout))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sectionLookup.TryGetValue(sectionId, out var sectionPlan))
            {
                continue;
            }

            if (sectionPlan.StructuralOnly)
            {
                draftedSectionIds.Add(sectionPlan.SectionId);
                continue;
            }

            sectionContexts.TryGetValue(sectionPlan.SectionId, out var context);
            var draft = await DraftSectionAsync(
                    rootTask,
                    sectionPlan,
                    findingMap,
                    aggregationResult,
                    synthesis,
                    outline,
                    context,
                    priorDrafts,
                    cancellationToken)
                .ConfigureAwait(false);
            drafts.Add(draft);
            draftedSectionIds.Add(sectionPlan.SectionId);
            priorDrafts.Add(draft);
        }

        foreach (var section in outline.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!draftedSectionIds.Add(section.SectionId))
            {
                continue;
            }

            if (section.StructuralOnly)
            {
                continue;
            }

            sectionContexts.TryGetValue(section.SectionId, out var context);
            var draft = await DraftSectionAsync(
                    rootTask,
                    section,
                    findingMap,
                    aggregationResult,
                    synthesis,
                    outline,
                    context,
                    priorDrafts,
                    cancellationToken)
                .ConfigureAwait(false);
            drafts.Add(draft);
            priorDrafts.Add(draft);
        }

        return drafts;
    }

    private static Dictionary<string, SectionDraftContext> BuildSectionContexts(
        IReadOnlyList<ReportLayoutNode> layout,
        IReadOnlyDictionary<string, ReportSectionPlan> sectionLookup)
    {
        var contexts = new Dictionary<string, SectionDraftContext>(StringComparer.OrdinalIgnoreCase);

        void Traverse(
            ReportLayoutNode node,
            List<string> path,
            ReportLayoutNode? parent,
            IReadOnlyList<ReportLayoutNode> siblings)
        {
            var nextPath = new List<string>(path)
            {
                node.Title
            };

            string? parentSectionId = parent?.SectionId;
            string? parentTitle = parent?.Title;
            string? parentSummary = null;
            if (!string.IsNullOrWhiteSpace(parentSectionId) && sectionLookup.TryGetValue(parentSectionId, out var parentPlan))
            {
                parentSummary = parentPlan.Summary;
            }

            var siblingTitles = siblings
                .Where(static s => !string.IsNullOrWhiteSpace(s.Title))
                .Where(s => !ReferenceEquals(s, node))
                .Select(static s => s.Title.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var childTitles = node.Children
                .Where(static c => !string.IsNullOrWhiteSpace(c.Title))
                .Select(static c => c.Title.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(node.SectionId))
            {
                contexts[node.SectionId] = new SectionDraftContext(
                    node.SectionId,
                    nextPath,
                    parentTitle,
                    parentSectionId,
                    parentSummary,
                    siblingTitles,
                    childTitles);
            }

            foreach (var child in node.Children)
            {
                Traverse(child, nextPath, node, node.Children);
            }
        }

        foreach (var root in layout)
        {
            Traverse(root, new List<string>(), null, layout);
        }

        return contexts;
    }

    private async Task<ReportSectionDraft> DraftSectionAsync(
        AgentTask rootTask,
        ReportSectionPlan sectionPlan,
        IReadOnlyDictionary<string, ResearchFinding> findingMap,
        ResearchAggregationResult aggregationResult,
    string synthesis,
        ReportOutline outline,
        SectionDraftContext? context,
        IReadOnlyList<ReportSectionDraft> priorDrafts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sectionPlan);

        var relevantFindings = new List<ResearchFinding>();
        foreach (string findingId in sectionPlan.SupportingFindingIds)
        {
            if (findingMap.TryGetValue(findingId, out var finding))
            {
                relevantFindings.Add(finding);
            }
        }

        if (relevantFindings.Count == 0)
        {
            relevantFindings = aggregationResult.Findings.Take(3).ToList();
        }

        var citations = relevantFindings
            .SelectMany(f => f.Citations)
            .GroupBy(c => c.SourceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(12)
            .ToList();

        var citationTagPairs = new List<(string Tag, SourceCitation Citation)>(citations.Count);
        for (int index = 0; index < citations.Count; index++)
        {
            citationTagPairs.Add(($"S{index + 1}", citations[index]));
        }

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            SystemPrompts.Templates.Common.ObjectiveLine,
            rootTask.Objective ?? string.Empty));
        promptBuilder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            SystemPrompts.Templates.Common.SectionLine,
            sectionPlan.Title));
        if (!string.IsNullOrWhiteSpace(sectionPlan.Summary))
        {
            promptBuilder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                SystemPrompts.Templates.Common.SectionGoalsLine,
                sectionPlan.Summary));
        }

        if (!string.IsNullOrWhiteSpace(outline.Notes))
        {
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineNotesHeader);
            promptBuilder.AppendLine(outline.Notes!.Trim());
        }

        if (context is not null)
        {
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineSectionLocationHeader);
            promptBuilder.AppendLine(string.Join(" > ", context.HeadingPath));

            if (!string.IsNullOrWhiteSpace(context.ParentTitle))
            {
                promptBuilder.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    SystemPrompts.Templates.Common.ParentSectionLine,
                    context.ParentTitle));
            }

            if (!string.IsNullOrWhiteSpace(context.ParentSummary))
            {
                promptBuilder.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    SystemPrompts.Templates.Common.ParentFocusLine,
                    context.ParentSummary));
            }

            if (context.SiblingTitles.Count > 0)
            {
                promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineSiblingHeader);
                foreach (string sibling in context.SiblingTitles)
                {
                    promptBuilder.Append("- ");
                    promptBuilder.AppendLine(sibling);
                }
            }

            if (context.ChildTitles.Count > 0)
            {
                promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineSubtopicsHeader);
                foreach (string child in context.ChildTitles)
                {
                    promptBuilder.Append("- ");
                    promptBuilder.AppendLine(child);
                }
            }
        }

        if (outline.Layout.Count > 0)
        {
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineStructureHeader);
            promptBuilder.AppendLine(RenderLayoutSummary(outline.Layout));
        }

        if (priorDrafts.Count > 0)
        {
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlinePreviousSectionsHeader);
            foreach (var prior in priorDrafts.TakeLast(3))
            {
                promptBuilder.Append("- ");
                promptBuilder.Append(prior.Title);
                promptBuilder.Append(": ");
                promptBuilder.AppendLine(SummarizeForPrompt(prior.Content, 320));
            }
        }

        if (!string.IsNullOrWhiteSpace(synthesis))
        {
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineSynthesisContextHeader);
            promptBuilder.AppendLine(synthesis.Trim());
        }

        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineEvidenceHeader);
        foreach (var finding in relevantFindings)
        {
            string evidence = finding.Content.Length > _options.MaxSectionEvidenceCharacters
                ? finding.Content[.._options.MaxSectionEvidenceCharacters]
                : finding.Content;
            promptBuilder.AppendLine($"- {finding.Title} (confidence {finding.Confidence:F2}): {evidence}");
        }

        if (relevantFindings.Count == 0)
        {
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.OutlineNoEvidence);
        }

        if (citationTagPairs.Count > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.SectionSourcesHeader);
            foreach (var pair in citationTagPairs)
            {
                string displayTitle = !string.IsNullOrWhiteSpace(pair.Citation.Title)
                    ? pair.Citation.Title!.Trim()
                    : pair.Citation.SourceId;
                promptBuilder.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    SystemPrompts.Templates.Orchestrator.SectionSourceLine,
                    pair.Tag,
                    displayTitle));

                if (!string.IsNullOrWhiteSpace(pair.Citation.Url))
                {
                    promptBuilder.AppendLine(string.Format(
                        CultureInfo.InvariantCulture,
                        SystemPrompts.Templates.Orchestrator.SectionSourceDetails,
                        pair.Citation.Url!.Trim()));
                }

                if (!string.IsNullOrWhiteSpace(pair.Citation.Snippet))
                {
                    promptBuilder.AppendLine(string.Format(
                        CultureInfo.InvariantCulture,
                        SystemPrompts.Templates.Orchestrator.SectionSourceSnippet,
                        pair.Citation.Snippet!.Trim()));
                }
            }

            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.SectionSourcesInstruction);
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.SectionWritingInstruction);

        var request = new OpenAiChatRequest(
            SystemPrompt: SystemPrompts.Orchestrator.SectionAuthor,
            Messages: new[]
            {
                new OpenAiChatMessage("user", promptBuilder.ToString())
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 600);

        string content = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            var fallbackSegments = new List<string>();
            if (!string.IsNullOrWhiteSpace(sectionPlan.Summary))
            {
                fallbackSegments.Add(sectionPlan.Summary!.Trim());
            }

            if (relevantFindings.Count > 0)
            {
                fallbackSegments.AddRange(relevantFindings
                    .Select(f => f.Content)
                    .Where(static text => !string.IsNullOrWhiteSpace(text))
                    .Select(static text => text.Trim()));
            }

            if (fallbackSegments.Count == 0)
            {
                fallbackSegments.Add($"Provide narrative detail for the section '{sectionPlan.Title}' connecting it to the research objective.");
            }

            content = string.Join(Environment.NewLine + Environment.NewLine, fallbackSegments);
        }

        var citationTagMap = citationTagPairs.Count == 0
            ? new Dictionary<string, SourceCitation>(StringComparer.OrdinalIgnoreCase)
            : citationTagPairs.ToDictionary(pair => pair.Tag, pair => pair.Citation, StringComparer.OrdinalIgnoreCase);

        return new ReportSectionDraft
        {
            SectionId = sectionPlan.SectionId,
            Title = sectionPlan.Title,
            Content = content.Trim(),
            Citations = citations,
            CitationTags = citationTagMap
        };
    }

    private static string RenderLayoutSummary(IReadOnlyList<ReportLayoutNode> layout)
    {
        if (layout.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        void RenderNode(ReportLayoutNode node, int depth)
        {
            if (string.IsNullOrWhiteSpace(node.Title))
            {
                return;
            }

            builder.Append(' ', depth * 2);
            builder.Append("- ");
            builder.AppendLine(node.Title.Trim());

            foreach (var child in node.Children)
            {
                RenderNode(child, depth + 1);
            }
        }

        foreach (var node in layout)
        {
            RenderNode(node, 0);
        }

        return builder.ToString().TrimEnd();
    }

    private static string SummarizeForPrompt(string content, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        string normalized = content.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + "…";
    }

    private static bool ShouldCreateSectionForNode(string title, string? parentTitle)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (string.Equals(title, "Executive Summary", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(title, "Sources", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task<string> GenerateReportAsync(
        AgentTask rootTask,
        ResearchAggregationResult aggregationResult,
        string synthesis,
        ReportOutline outline,
        IReadOnlyList<ReportSectionDraft> sectionDrafts,
        CancellationToken cancellationToken)
    {
        string rootQuestion = string.IsNullOrWhiteSpace(rootTask.Objective)
            ? "Autonomous Research Report"
            : rootTask.Objective!;
        string markdown = _reportBuilder.Build(rootQuestion, synthesis, outline, sectionDrafts, aggregationResult.Findings);
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
            promptBuilder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                SystemPrompts.Templates.Common.ObjectiveLine,
                rootTask.Objective ?? string.Empty));
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.ReportLinesHeader);
            promptBuilder.AppendLine(enumerated);
            promptBuilder.AppendLine();
            promptBuilder.AppendLine(SystemPrompts.Templates.Orchestrator.ReportRevisionInstruction);

            var request = new OpenAiChatRequest(
                SystemPrompt: SystemPrompts.Orchestrator.ReportEditor,
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

    private bool TryParseOutline(
        string response,
        string objective,
        IReadOnlyList<ResearchFinding> findings,
        out ReportOutline outline)
    {
        outline = new ReportOutline
        {
            Objective = objective
        };
        var workingOutline = outline;

        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        int start = response.IndexOf('{');
        int end = response.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return false;
        }

        string json = response[start..(end + 1)];
        try
        {
            var payload = JsonSerializer.Deserialize<OutlinePayload>(json, _revisionOptions);
            if (payload is null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(payload.Title))
            {
                outline.Title = payload.Title!.Trim();
            }

            if (!string.IsNullOrWhiteSpace(payload.Notes))
            {
                outline.Notes = payload.Notes!.Trim();
            }

            var validFindingIds = new HashSet<string>(findings.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
            var declaredSectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (payload.Sections is not null)
            {
                foreach (var section in payload.Sections)
                {
                    if (section is null || string.IsNullOrWhiteSpace(section.Title))
                    {
                        continue;
                    }

                    string sectionId = string.IsNullOrWhiteSpace(section.SectionId)
                        ? Guid.NewGuid().ToString("N")
                        : section.SectionId!.Trim();

                    if (!declaredSectionIds.Add(sectionId))
                    {
                        sectionId = Guid.NewGuid().ToString("N");
                        declaredSectionIds.Add(sectionId);
                    }

                    var plan = new ReportSectionPlan
                    {
                        SectionId = sectionId,
                        Title = section.Title!.Trim(),
                        Summary = string.IsNullOrWhiteSpace(section.Summary) ? null : section.Summary!.Trim(),
                        StructuralOnly = section.StructuralOnly ?? false
                    };

                    if (section.SupportingFindingIds is not null)
                    {
                        foreach (string id in section.SupportingFindingIds)
                        {
                            if (!string.IsNullOrWhiteSpace(id) && validFindingIds.Contains(id))
                            {
                                plan.SupportingFindingIds.Add(id);
                            }
                        }
                    }

                    outline.Sections.Add(plan);
                }
            }

            static string NormalizeHeading(string? heading)
            {
                if (string.IsNullOrWhiteSpace(heading))
                {
                    return "h2";
                }

                return heading.Trim().ToLowerInvariant() switch
                {
                    "h1" or "#" => "h1",
                    "h3" or "###" => "h3",
                    "h4" or "####" => "h4",
                    _ => "h2"
                };
            }

            List<ReportLayoutNode> ConvertNodes(List<OutlineLayoutNodePayload>? nodes, string? parentTitle)
            {
                var list = new List<ReportLayoutNode>();
                if (nodes is null)
                {
                    return list;
                }

                foreach (var node in nodes)
                {
                    if (node is null || string.IsNullOrWhiteSpace(node.Title))
                    {
                        continue;
                    }

                    string nodeId = string.IsNullOrWhiteSpace(node.NodeId)
                        ? Guid.NewGuid().ToString("N")
                        : node.NodeId!.Trim();

                    string? sectionId = string.IsNullOrWhiteSpace(node.SectionId)
                        ? null
                        : node.SectionId!.Trim();

                    if (sectionId is not null && !declaredSectionIds.Contains(sectionId))
                    {
                        sectionId = null;
                    }

                    if (sectionId is null && ShouldCreateSectionForNode(node.Title!, parentTitle))
                    {
                        var syntheticPlan = new ReportSectionPlan
                        {
                            Title = node.Title!.Trim()
                        };

                        workingOutline.Sections.Add(syntheticPlan);
                        declaredSectionIds.Add(syntheticPlan.SectionId);
                        sectionId = syntheticPlan.SectionId;
                    }

                    var layoutNode = new ReportLayoutNode
                    {
                        NodeId = nodeId,
                        HeadingType = NormalizeHeading(node.HeadingType),
                        Title = node.Title!.Trim(),
                        SectionId = sectionId
                    };

                    var children = ConvertNodes(node.Children, layoutNode.Title);
                    if (children.Count > 0)
                    {
                        layoutNode.Children.AddRange(children);
                    }

                    list.Add(layoutNode);
                }

                return list;
            }

            if (payload.Layout is not null)
            {
                outline.Layout.AddRange(ConvertNodes(payload.Layout, null));
            }

            if (string.IsNullOrWhiteSpace(outline.Title))
            {
                outline.Title = string.IsNullOrWhiteSpace(objective)
                    ? "Autonomous Research Report"
                    : objective.Trim();
            }

            return outline.Layout.Count > 0;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse outline response. Raw response: {Response}", response);
            return false;
        }
    }

    private static ReportOutline BuildFallbackOutline(string objective, IReadOnlyList<ResearchFinding> findings)
    {
        string rootTitle = string.IsNullOrWhiteSpace(objective) ? "Autonomous Research Report" : objective.Trim();

        var outline = new ReportOutline
        {
            Objective = objective,
            Title = rootTitle,
            Notes = "Fallback outline generated due to parsing issues."
        };

        var summaryPlan = new ReportSectionPlan
        {
            Title = "Executive Summary"
        };
        outline.Sections.Add(summaryPlan);

        outline.Layout.Add(new ReportLayoutNode
        {
            HeadingType = "h2",
            Title = "Executive Summary",
            SectionId = summaryPlan.SectionId
        });

        if (findings.Count == 0)
        {
            var overviewPlan = new ReportSectionPlan
            {
                Title = "Key Insights"
            };
            outline.Sections.Add(overviewPlan);

            outline.Layout.Add(new ReportLayoutNode
            {
                HeadingType = "h2",
                Title = "Key Insights",
                SectionId = overviewPlan.SectionId
            });
        }
        else
        {
            var bodyNode = new ReportLayoutNode
            {
                HeadingType = "h2",
                Title = "Key Findings"
            };

            foreach (var finding in findings.Take(4))
            {
                var plan = new ReportSectionPlan
                {
                    Title = finding.Title
                };
                plan.SupportingFindingIds.Add(finding.Id);
                outline.Sections.Add(plan);

                bodyNode.Children.Add(new ReportLayoutNode
                {
                    HeadingType = "h3",
                    Title = finding.Title,
                    SectionId = plan.SectionId
                });
            }

            if (findings.Count > 4)
            {
                var contextPlan = new ReportSectionPlan
                {
                    Title = "Broader Context"
                };

                foreach (var id in findings.Skip(4).Select(f => f.Id).Take(5))
                {
                    contextPlan.SupportingFindingIds.Add(id);
                }

                outline.Sections.Add(contextPlan);
                bodyNode.Children.Add(new ReportLayoutNode
                {
                    HeadingType = "h3",
                    Title = contextPlan.Title,
                    SectionId = contextPlan.SectionId
                });
            }

            outline.Layout.Add(bodyNode);
        }

        outline.Layout.Add(new ReportLayoutNode
        {
            HeadingType = "h2",
            Title = "Sources"
        });
        return outline;
    }

    private static OpenAiChatJsonSchemaFormat BuildOutlineSchemaFormat()
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object>
            {
                ["title"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "Concise Markdown-ready report title."
                },
                ["notes"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "Optional editorial notes for the outline. Use an empty string when no notes are required."
                },
                ["sections"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "Detailed section plans that will be used for drafting.",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["$ref"] = "#/$defs/sectionPlan"
                    },
                    ["minItems"] = 1
                },
                ["layout"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "Hierarchical heading layout describing report structure.",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["$ref"] = "#/$defs/layoutNode"
                    },
                    ["minItems"] = 1
                }
            },
            ["required"] = new[] { "title", "notes", "sections", "layout" },
            ["$defs"] = new Dictionary<string, object>
            {
                ["sectionPlan"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["sectionId"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Unique identifier for the section."
                        },
                        ["title"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Heading text for the section."
                        },
                        ["summary"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Brief summary describing the focus of the section.",
                            ["default"] = string.Empty
                        },
                        ["supportingFindingIds"] = new Dictionary<string, object>
                        {
                            ["type"] = "array",
                            ["description"] = "List of finding IDs that directly support this section.",
                            ["items"] = new Dictionary<string, object>
                            {
                                ["type"] = "string"
                            },
                            ["minItems"] = 0
                        },
                        ["structuralOnly"] = new Dictionary<string, object>
                        {
                            ["type"] = "boolean",
                            ["description"] = "When true, the section represents a structural element that must not include drafted narrative paragraphs.",
                            ["default"] = false
                        }
                    },
                    ["required"] = new[] { "sectionId", "title", "summary", "supportingFindingIds", "structuralOnly" }
                },
                ["layoutNode"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["nodeId"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Unique identifier for the layout node."
                        },
                        ["headingType"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Markdown heading level (h1-h4).",
                            ["pattern"] = "^h[1-4]$"
                        },
                        ["title"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Heading title rendered for this node."
                        },
                        ["sectionId"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional sectionId this layout node maps to."
                        },
                        ["children"] = new Dictionary<string, object>
                        {
                            ["type"] = "array",
                            ["items"] = new Dictionary<string, object>
                            {
                                ["$ref"] = "#/$defs/layoutNode"
                            },
                            ["default"] = Array.Empty<object>(),
                            ["description"] = "Child nodes nested under this heading."
                        }
                    },
                    ["required"] = new[] { "nodeId", "headingType", "title", "sectionId", "children" }
                }
            }
        };

        JsonNode? schemaNode = JsonSerializer.SerializeToNode(schema);
        if (schemaNode is null)
        {
            throw new InvalidOperationException("Failed to materialize outline schema JSON node.");
        }

        return new OpenAiChatJsonSchemaFormat(
            "report_outline",
            schemaNode,
            Strict: true);
    }

    private bool TryParseContinuationDecision(string response, out ResearchContinuationDecision decision)
    {
        decision = new ResearchContinuationDecision(false, Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        int start = response.IndexOf('{');
        int end = response.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return false;
        }

        string json = response[start..(end + 1)];
        try
        {
            var parsed = JsonSerializer.Deserialize<ContinuationDecisionPayload>(json, _revisionOptions);
            if (parsed is null)
            {
                return false;
            }

            var questions = parsed.FollowUpQuestions?
                .Where(static question => !string.IsNullOrWhiteSpace(question))
                .Select(static question => question.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(_options.MaxFollowupQuestions)
                .ToList() ?? new List<string>();

            decision = new ResearchContinuationDecision(parsed.Continue, questions);
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse continuation decision. Raw response: {Response}", response);
            return false;
        }
    }

    private static bool IsAffirmative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("y", StringComparison.OrdinalIgnoreCase);
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

    private sealed class OutlinePayload
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("sections")]
        public List<OutlineSectionPayload>? Sections { get; set; }

        [JsonPropertyName("layout")]
        public List<OutlineLayoutNodePayload>? Layout { get; set; }
    }

    private sealed class OutlineSectionPayload
    {
        [JsonPropertyName("sectionId")]
        public string? SectionId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("supportingFindingIds")]
        public List<string>? SupportingFindingIds { get; set; }

        [JsonPropertyName("structuralOnly")]
        public bool? StructuralOnly { get; set; }
    }

    private sealed class OutlineLayoutNodePayload
    {
        [JsonPropertyName("nodeId")]
        public string? NodeId { get; set; }

        [JsonPropertyName("headingType")]
        public string? HeadingType { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("sectionId")]
        public string? SectionId { get; set; }

        [JsonPropertyName("children")]
        public List<OutlineLayoutNodePayload>? Children { get; set; }
    }

    private sealed class ContinuationDecisionPayload
    {
        [JsonPropertyName("continue")]
        public bool Continue { get; set; }

        [JsonPropertyName("followUpQuestions")]
        public List<string>? FollowUpQuestions { get; set; }
    }

    private sealed record ResearchContinuationDecision(bool ShouldContinue, IReadOnlyList<string> NewQuestions);

    private sealed class ReportEditInstruction
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("line")]
        public int Line { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed record SectionDraftContext(
        string SectionId,
        IReadOnlyList<string> HeadingPath,
        string? ParentTitle,
        string? ParentSectionId,
        string? ParentSummary,
        IReadOnlyList<string> SiblingTitles,
        IReadOnlyList<string> ChildTitles);
}
















