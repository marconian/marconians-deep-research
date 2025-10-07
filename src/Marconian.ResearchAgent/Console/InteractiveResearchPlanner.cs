using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Marconian.ResearchAgent.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.ConsoleApp;

internal sealed class InteractiveResearchPlanner
{
    private static readonly JsonNode PlannerResponseSchema = JsonNode.Parse(
        """
        {
            "type": "object",
            "required": ["action", "summary", "keyQuestions", "proposedPlan", "followUp", "notes"],
            "properties": {
                "action": {
                    "type": "string",
                    "enum": ["ask", "plan", "abort"]
                },
                "summary": {
                    "type": "string"
                },
                "keyQuestions": {
                    "type": "array",
                    "items": { "type": "string" }
                },
                "proposedPlan": {
                    "type": "array",
                    "items": { "type": "string" }
                },
                "followUp": {
                    "type": "string"
                },
                "notes": {
                    "type": "string"
                }
            },
            "additionalProperties": false
        }
        """)!;

    private readonly IAzureOpenAiService _openAiService;
    private readonly string _chatDeploymentName;
    private readonly ILogger<InteractiveResearchPlanner> _logger;
    private readonly IThoughtEventPublisher _thoughtPublisher;
    private const string PlannerPhase = "Planning";

    public InteractiveResearchPlanner(
        IAzureOpenAiService openAiService,
        string chatDeploymentName,
        ILogger<InteractiveResearchPlanner>? logger = null,
        IThoughtEventPublisher? thoughtPublisher = null)
    {
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _chatDeploymentName = string.IsNullOrWhiteSpace(chatDeploymentName)
            ? throw new ArgumentException("Chat deployment name must be provided.", nameof(chatDeploymentName))
            : chatDeploymentName;
        _logger = logger ?? NullLogger<InteractiveResearchPlanner>.Instance;
        _thoughtPublisher = thoughtPublisher ?? NullThoughtEventPublisher.Instance;
    }

    private void PublishThought(string detail, ThoughtSeverity severity = ThoughtSeverity.Info)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        _thoughtPublisher.Publish(PlannerPhase, nameof(InteractiveResearchPlanner), LimitDetail(detail), severity, null);
    }

    private Task<string> StreamPlannerCompletionAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
        => ThoughtStreamingHelper.StreamCompletionAsync(
            _openAiService,
            request,
            _thoughtPublisher,
            PlannerPhase,
            nameof(InteractiveResearchPlanner),
            correlationId: null,
            severity: ThoughtSeverity.Info,
            cancellationToken: cancellationToken,
            detailFormatter: detail => LimitDetail(detail));

    private static string LimitDetail(string value, int maxLength = 220)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength].TrimEnd() + "…";
    }

    public async Task<PlannerOutcome?> RunAsync(string objective, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            throw new ArgumentException("Objective must be provided.", nameof(objective));
        }

        string currentObjective = objective.Trim();
        PublishThought($"Interactive planner engaged for '{currentObjective}'.");
        var history = new List<OpenAiChatMessage>
        {
            new("user", $"Research objective: {currentObjective}")
        };

        PlannerResponse response = await InvokePlannerAsync(history, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (response.Action)
            {
                case PlannerAction.RequestClarification:
                    DisplayClarificationRequest(response, currentObjective);
                    Console.Write("Your answer (leave blank to cancel): ");
                    string? clarification = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
                    if (string.IsNullOrWhiteSpace(clarification))
                    {
                        Console.WriteLine("Planner cancelled. Returning to menu.");
                        PublishThought("Planner cancelled by user during clarification phase.", ThoughtSeverity.Warning);
                        return null;
                    }

                    if (string.Equals(clarification, "accept", StringComparison.OrdinalIgnoreCase))
                    {
                        history.Add(new("user", "Proceed with this plan using your recommended assumptions. No further clarifications available."));
                        response = await InvokePlannerAsync(history, cancellationToken).ConfigureAwait(false);

                        if (response.Action == PlannerAction.PlanReady)
                        {
                            DisplayPlan(response, currentObjective);
                            Console.WriteLine();
                            Console.WriteLine("Plan accepted. Continuing with research.");
                            PublishThought($"Plan accepted after clarification with {response.ProposedPlan.Count} steps.");
                            return new PlannerOutcome(
                                currentObjective,
                                response.Summary,
                                response.KeyQuestions,
                                BuildContextHints(response));
                        }

                        continue;
                    }

                    history.Add(new("user", $"Clarification: {clarification}"));
                    response = await InvokePlannerAsync(history, cancellationToken).ConfigureAwait(false);
                    continue;

                case PlannerAction.PlanReady:
                    DisplayPlan(response, currentObjective);
                    Console.WriteLine();
                    Console.Write("Action [C=confirm, R=refine, E=edit objective, A=abort]: ");
                    string? choice = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim().ToLowerInvariant();
                    switch (choice)
                    {
                        case "c":
                        case "confirm":
                            PublishThought($"Plan confirmed interactively with {response.ProposedPlan.Count} steps.");
                            return new PlannerOutcome(
                                currentObjective,
                                response.Summary,
                                response.KeyQuestions,
                                BuildContextHints(response));
                        case "r":
                        case "refine":
                            Console.Write("Describe the refinement you want: ");
                            string? refinement = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
                            if (string.IsNullOrWhiteSpace(refinement))
                            {
                                Console.WriteLine("No refinement entered. Keeping current plan draft.");
                                continue;
                            }

                            history.Add(new("user", $"Refine the plan with these adjustments: {refinement}"));
                            response = await InvokePlannerAsync(history, cancellationToken).ConfigureAwait(false);
                            continue;
                        case "e":
                        case "edit":
                            Console.Write("Enter the updated research objective: ");
                            string? updatedObjective = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
                            if (string.IsNullOrWhiteSpace(updatedObjective))
                            {
                                Console.WriteLine("Objective unchanged.");
                                continue;
                            }

                            currentObjective = updatedObjective;
                            history.Add(new("user", $"Updated research objective: {currentObjective}"));
                            response = await InvokePlannerAsync(history, cancellationToken).ConfigureAwait(false);
                            continue;
                        case "a":
                        case "abort":
                            Console.WriteLine("Planner aborted at user request.");
                            PublishThought("Planner aborted at user request.", ThoughtSeverity.Warning);
                            return null;
                        default:
                            Console.WriteLine("Unrecognized action. Please choose C, R, E, or A.");
                            continue;
                    }

                case PlannerAction.Abort:
                    Console.WriteLine("Planner aborted due to insufficient detail.");
                    PublishThought("Planner aborted due to insufficient detail.", ThoughtSeverity.Warning);
                    return null;

                default:
                    throw new InvalidOperationException($"Unhandled planner action {response.Action}.");
            }
        }
    }

    public async Task<PlannerOutcome> RunNonInteractiveAsync(string objective, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            throw new ArgumentException("Objective must be provided.", nameof(objective));
        }

        string trimmedObjective = objective.Trim();
        PublishThought($"Non-interactive planner invoked for '{trimmedObjective}'.");
        var history = new List<OpenAiChatMessage>
        {
            new("user", $"Research objective: {trimmedObjective}. Provide a concise plan and key questions.")
        };

        PlannerResponse response = await InvokePlannerAsync(history, cancellationToken).ConfigureAwait(false);
        if (response.Action == PlannerAction.RequestClarification)
        {
            history.Add(new("user", "No additional clarification is available. Proceed with the best possible plan."));
            response = await InvokePlannerAsync(history, cancellationToken).ConfigureAwait(false);
        }

        if (response.Action == PlannerAction.Abort)
        {
            PublishThought("Planner could not produce a plan for the provided objective.", ThoughtSeverity.Warning);
            throw new InvalidOperationException("Planner could not produce a plan for the provided objective.");
        }

        PublishThought($"Planner completed non-interactive pass with {response.ProposedPlan.Count} steps.");
        return new PlannerOutcome(trimmedObjective, response.Summary, response.KeyQuestions, BuildContextHints(response));
    }

    private async Task<PlannerResponse> InvokePlannerAsync(List<OpenAiChatMessage> history, CancellationToken cancellationToken)
    {
        var request = new OpenAiChatRequest(
            SystemPrompts.Planner.Coordinator,
            history,
            _chatDeploymentName,
            JsonSchemaFormat: new OpenAiChatJsonSchemaFormat("planner_response", PlannerResponseSchema));

    string response = await StreamPlannerCompletionAsync(request, cancellationToken).ConfigureAwait(false);
    _logger.LogTrace("Planner response payload: {Payload}", response);
    PlannerResponse parsed = PlannerResponse.Parse(response);
    PublishThought($"Planner action: {parsed.Action} (Summary preview: {LimitDetail(parsed.Summary ?? string.Empty, 120)})");
    return parsed;
    }

    private static IReadOnlyList<string> BuildContextHints(PlannerResponse response)
    {
        var hints = new List<string>
        {
            $"PlannerSummary: {response.Summary}"
        };

        if (response.ProposedPlan.Count > 0)
        {
            int step = 1;
            foreach (string planStep in response.ProposedPlan)
            {
                hints.Add($"PlannerPlanStep{step++}: {planStep}");
            }
        }

        if (response.KeyQuestions.Count > 0)
        {
            hints.AddRange(response.KeyQuestions.Select(question => $"PlannerQuestion: {question}"));
        }

        if (!string.IsNullOrWhiteSpace(response.Notes))
        {
            hints.Add($"PlannerNotes: {response.Notes}");
        }

        return hints;
    }

    private static void DisplayPlan(PlannerResponse response, string objective)
    {
        Console.WriteLine();
        Console.WriteLine("# Proposed Research Plan");
        Console.WriteLine($"Objective: {objective}");
        Console.WriteLine();
        Console.WriteLine(response.Summary);
        if (response.ProposedPlan.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Proposed Research Plan:");
            int index = 1;
            foreach (string step in response.ProposedPlan)
            {
                Console.WriteLine($" {index++}. {step}");
            }
        }
        if (!string.IsNullOrWhiteSpace(response.Notes))
        {
            Console.WriteLine();
            Console.WriteLine("Notes:");
            Console.WriteLine(response.Notes);
        }

        if (response.KeyQuestions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Key Questions to Investigate:");
            int index = 1;
            foreach (string question in response.KeyQuestions)
            {
                Console.WriteLine($" {index++}. {question}");
            }
        }
    }

    private static void DisplayClarificationRequest(PlannerResponse response, string objective)
    {
        Console.WriteLine();
        Console.WriteLine("# Planner Clarification Needed");
        if (!string.IsNullOrWhiteSpace(objective))
        {
            Console.WriteLine($"Objective: {objective}");
        }

        if (!string.IsNullOrWhiteSpace(response.Summary))
        {
            Console.WriteLine();
            Console.WriteLine("Summary:");
            Console.WriteLine(response.Summary);
        }

        if (response.ProposedPlan.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Current Draft Plan:");
            int index = 1;
            foreach (string step in response.ProposedPlan)
            {
                Console.WriteLine($" {index++}. {step}");
            }
        }

        if (response.KeyQuestions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Optional Clarification Questions (answer any to refine the plan):");
            int index = 1;
            foreach (string question in response.KeyQuestions)
            {
                Console.WriteLine($" {index++}. {question}");
            }
        }

        if (!string.IsNullOrWhiteSpace(response.Notes))
        {
            Console.WriteLine();
            Console.WriteLine("Notes:");
            Console.WriteLine(response.Notes);
        }

        Console.WriteLine();
        if (!string.IsNullOrWhiteSpace(response.FollowUp))
        {
            Console.WriteLine(response.FollowUp);
            Console.WriteLine();
        }

        Console.WriteLine("Type your clarifications, or enter 'accept' to proceed with the plan using these assumptions.");
        Console.WriteLine();
    }

    private static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => await Task.Run(Console.ReadLine, cancellationToken).ConfigureAwait(false);

    private enum PlannerAction
    {
        RequestClarification,
        PlanReady,
        Abort
    }

    private sealed record PlannerResponse(PlannerAction Action, string Summary, IReadOnlyList<string> KeyQuestions, IReadOnlyList<string> ProposedPlan, string? FollowUp, string? Notes)
    {
        public static PlannerResponse Parse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new PlannerResponse(PlannerAction.Abort, string.Empty, Array.Empty<string>(), Array.Empty<string>(), null, null);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(content);
                JsonElement root = document.RootElement;
                string action = root.GetProperty("action").GetString() ?? string.Empty;
                string summary = root.GetProperty("summary").GetString() ?? string.Empty;
                var keyQuestions = new List<string>();
                if (root.TryGetProperty("keyQuestions", out var questionsElement) && questionsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement element in questionsElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                        {
                            keyQuestions.Add(element.GetString() ?? string.Empty);
                        }
                    }
                }

                var proposedPlan = new List<string>();
                if (root.TryGetProperty("proposedPlan", out var planElement) && planElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement element in planElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                        {
                            string? value = element.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                proposedPlan.Add(value);
                            }
                        }
                    }
                }
                else
                {
                    proposedPlan.Add("Outline key steps for this research plan.");
                }

                string? followUp = null;
                if (root.TryGetProperty("followUp", out var followUpElement) && followUpElement.ValueKind == JsonValueKind.String)
                {
                    string? raw = followUpElement.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        followUp = raw;
                    }
                }

                string? notes = null;
                if (root.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.String)
                {
                    string? raw = notesElement.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        notes = raw;
                    }
                }

                IReadOnlyList<string> planSegments = proposedPlan.Count == 0 ? Array.Empty<string>() : proposedPlan;

                return action switch
                {
                    "ask" => new PlannerResponse(PlannerAction.RequestClarification, summary, keyQuestions, planSegments, followUp, notes),
                    "plan" => new PlannerResponse(PlannerAction.PlanReady, summary, keyQuestions, planSegments, followUp, notes),
                    "abort" => new PlannerResponse(PlannerAction.Abort, summary, keyQuestions, planSegments, followUp, notes),
                    _ => new PlannerResponse(PlannerAction.PlanReady, summary, keyQuestions, planSegments, followUp, notes)
                };
            }
            catch (JsonException)
            {
                return new PlannerResponse(PlannerAction.Abort, string.Empty, Array.Empty<string>(), Array.Empty<string>(), null, null);
            }
        }
    }
}

internal sealed record PlannerOutcome(
    string Objective,
    string PlanSummary,
    IReadOnlyList<string> KeyQuestions,
    IReadOnlyList<string> ContextHints);