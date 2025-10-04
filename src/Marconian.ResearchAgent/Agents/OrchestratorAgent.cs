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
    private const int MaxResearchPasses = 3;
    private const int MaxFollowupQuestions = 3;
    private const int MaxSectionEvidenceCharacters = 2400;

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

            CurrentState = OrchestratorState.ExecutingResearchBranches;
            aggregationResult = await RunResearchWorkflowAsync(task, plan, branchResults, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Completed research workflow with {FindingCount} aggregated findings across {BranchCount} branches.",
                aggregationResult.Findings.Count,
                branchResults.Count);
            await _longTermMemoryManager.UpsertSessionStateAsync(
                task.ResearchSessionId,
                "branches_completed",
                $"Branches: {branchResults.Count}",
                cancellationToken).ConfigureAwait(false);

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

        for (int pass = 0; pass < MaxResearchPasses; pass++)
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
                .Take(MaxFollowupQuestions)
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
        if (passNumber >= MaxResearchPasses)
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
                string content = finding.Content.Length > MaxSectionEvidenceCharacters
                    ? finding.Content[..MaxSectionEvidenceCharacters]
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
            findingBuilder.AppendLine("No consolidated findings yet.");
        }

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine($"Research objective: {task.Objective}");
        promptBuilder.AppendLine($"Current pass: {passNumber} of {MaxResearchPasses}.");
        promptBuilder.AppendLine("Current branch status:");
        promptBuilder.AppendLine(planSummaryBuilder.ToString());
        promptBuilder.AppendLine("Consolidated findings so far:");
        promptBuilder.AppendLine(findingBuilder.ToString());
        promptBuilder.AppendLine("Should the research continue? If important gaps remain, propose up to three highly specific follow-up questions.");
        promptBuilder.AppendLine("Respond with JSON: {\"continue\": boolean, \"followUpQuestions\": [string...]}. Explain nothing else.");

        var request = new OpenAiChatRequest(
            SystemPrompt: "You are a research director who decides when an investigation should continue and proposes precise follow-up questions when needed.",
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

    private async Task<ReportOutline> GenerateOutlineAsync(
        AgentTask rootTask,
        ResearchAggregationResult aggregationResult,
        string synthesis,
        CancellationToken cancellationToken)
    {
        var findings = aggregationResult.Findings ?? Array.Empty<ResearchFinding>();
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine($"Objective: {rootTask.Objective}");
        promptBuilder.AppendLine("Synthesis overview:");
        promptBuilder.AppendLine(string.IsNullOrWhiteSpace(synthesis) ? "(No synthesis provided.)" : synthesis.Trim());
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Findings (use IDs when referencing support):");

        foreach (var finding in findings.Take(10))
        {
            string content = finding.Content.Length > MaxSectionEvidenceCharacters
                ? finding.Content[..MaxSectionEvidenceCharacters]
                : finding.Content;
            promptBuilder.AppendLine($"- Id: {finding.Id}");
            promptBuilder.AppendLine($"  Title: {finding.Title}");
            promptBuilder.AppendLine($"  Confidence: {finding.Confidence:F2}");
            promptBuilder.AppendLine($"  Summary: {content}");
            promptBuilder.AppendLine();
        }

        if (findings.Count == 0)
        {
            promptBuilder.AppendLine("(No findings available. Create a generic outline.)");
        }

        promptBuilder.AppendLine("Design a structured outline for the final report. Identify core sections that directly answer the objective, and optional general sections for context (introduction, methodology, risks, open questions). Return JSON with fields: 'notes' (string), 'coreSections' (array), 'generalSections' (array). Each section must include 'title', 'summary', and 'supportingFindingIds' referencing the provided IDs. Use empty arrays when not needed. No extra commentary.");

        var request = new OpenAiChatRequest(
            SystemPrompt: "You are a veteran research editor who designs structured report outlines grounded in provided evidence.",
            Messages: new[]
            {
                new OpenAiChatMessage("user", promptBuilder.ToString())
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 500);

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

        foreach (var section in outline.CoreSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var draft = await DraftSectionAsync(rootTask, section, findingMap, aggregationResult, synthesis, isGeneral: false, cancellationToken).ConfigureAwait(false);
            drafts.Add(draft);
        }

        foreach (var section in outline.GeneralSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var draft = await DraftSectionAsync(rootTask, section, findingMap, aggregationResult, synthesis, isGeneral: true, cancellationToken).ConfigureAwait(false);
            drafts.Add(draft);
        }

        return drafts;
    }

    private async Task<ReportSectionDraft> DraftSectionAsync(
        AgentTask rootTask,
        ReportSectionPlan sectionPlan,
        IReadOnlyDictionary<string, ResearchFinding> findingMap,
        ResearchAggregationResult aggregationResult,
        string synthesis,
        bool isGeneral,
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

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine($"Objective: {rootTask.Objective}");
        promptBuilder.AppendLine($"Section: {sectionPlan.Title}");
        if (!string.IsNullOrWhiteSpace(sectionPlan.Summary))
        {
            promptBuilder.AppendLine($"Section goals: {sectionPlan.Summary}");
        }

        if (!string.IsNullOrWhiteSpace(synthesis))
        {
            promptBuilder.AppendLine("Overall synthesis context:");
            promptBuilder.AppendLine(synthesis.Trim());
        }

        promptBuilder.AppendLine("Evidence:");
        foreach (var finding in relevantFindings)
        {
            string evidence = finding.Content.Length > MaxSectionEvidenceCharacters
                ? finding.Content[..MaxSectionEvidenceCharacters]
                : finding.Content;
            promptBuilder.AppendLine($"- {finding.Title} (confidence {finding.Confidence:F2}): {evidence}");
        }

        if (relevantFindings.Count == 0)
        {
            promptBuilder.AppendLine("(No direct evidence; provide contextual overview.)");
        }

        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Write a polished Markdown narrative (2-4 paragraphs) that explains this section. Stay factual and only use the supplied evidence. Do not include headings or citation brackets. Return only the body text.");

        var request = new OpenAiChatRequest(
            SystemPrompt: "You craft concise, evidence-grounded research report sections.",
            Messages: new[]
            {
                new OpenAiChatMessage("user", promptBuilder.ToString())
            },
            DeploymentName: _chatDeploymentName,
            MaxOutputTokens: 600);

        string content = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            content = string.Join(
                Environment.NewLine + Environment.NewLine,
                relevantFindings.Select(f => f.Content));
        }

        var citations = relevantFindings
            .SelectMany(f => f.Citations)
            .GroupBy(c => c.SourceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(12)
            .ToList();

        return new ReportSectionDraft
        {
            SectionId = sectionPlan.SectionId,
            Title = sectionPlan.Title,
            Content = content.Trim(),
            IsGeneral = isGeneral,
            Citations = citations
        };
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
        var result = outline;

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

            if (!string.IsNullOrWhiteSpace(payload.Notes))
            {
                result.Notes = payload.Notes!.Trim();
            }

            var validIds = new HashSet<string>(findings.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);

            void AddSections(IEnumerable<OutlineSectionPayload>? sections, bool isGeneral)
            {
                if (sections is null)
                {
                    return;
                }

                foreach (var section in sections)
                {
                    if (section is null || string.IsNullOrWhiteSpace(section.Title))
                    {
                        continue;
                    }

                    var plan = new ReportSectionPlan
                    {
                        Title = section.Title.Trim(),
                        Summary = string.IsNullOrWhiteSpace(section.Summary) ? null : section.Summary!.Trim()
                    };

                    if (section.SupportingFindingIds is not null)
                    {
                        foreach (string id in section.SupportingFindingIds)
                        {
                            if (!string.IsNullOrWhiteSpace(id) && validIds.Contains(id))
                            {
                                plan.SupportingFindingIds.Add(id);
                            }
                        }
                    }

                    if (isGeneral)
                    {
                        result.GeneralSections.Add(plan);
                    }
                    else
                    {
                        result.CoreSections.Add(plan);
                    }
                }
            }

            AddSections(payload.CoreSections, isGeneral: false);
            AddSections(payload.GeneralSections, isGeneral: true);

            return result.CoreSections.Count > 0 || result.GeneralSections.Count > 0 || !string.IsNullOrWhiteSpace(result.Notes);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse outline response. Raw response: {Response}", response);
            return false;
        }
    }

    private static ReportOutline BuildFallbackOutline(string objective, IReadOnlyList<ResearchFinding> findings)
    {
        var outline = new ReportOutline
        {
            Objective = objective,
            Notes = "Fallback outline generated due to parsing issues."
        };

        if (findings.Count == 0)
        {
            outline.CoreSections.Add(new ReportSectionPlan
            {
                Title = "Key Insights",
                Summary = "Summarize the research objective, key hypotheses, and next steps."
            });
            outline.GeneralSections.Add(new ReportSectionPlan
            {
                Title = "Recommended Next Actions",
                Summary = "Outline pragmatic follow-up research tasks to fill evidence gaps."
            });

            return outline;
        }

        foreach (var finding in findings.Take(4))
        {
            var plan = new ReportSectionPlan
            {
                Title = finding.Title,
                Summary = finding.Content.Length > 200 ? finding.Content[..200] + "…" : finding.Content
            };
            plan.SupportingFindingIds.Add(finding.Id);
            outline.CoreSections.Add(plan);
        }

        if (findings.Count > 4)
        {
            var plan = new ReportSectionPlan
            {
                Title = "Broader Context",
                Summary = "Highlight secondary findings, risks, or adjacent themes uncovered during research."
            };
            foreach (var id in findings.Skip(4).Select(f => f.Id).Take(5))
            {
                plan.SupportingFindingIds.Add(id);
            }

            outline.GeneralSections.Add(plan);
        }

        return outline;
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
                .Take(MaxFollowupQuestions)
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
        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("coreSections")]
        public List<OutlineSectionPayload>? CoreSections { get; set; }

        [JsonPropertyName("generalSections")]
        public List<OutlineSectionPayload>? GeneralSections { get; set; }
    }

    private sealed class OutlineSectionPayload
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("supportingFindingIds")]
        public List<string>? SupportingFindingIds { get; set; }
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
}
















