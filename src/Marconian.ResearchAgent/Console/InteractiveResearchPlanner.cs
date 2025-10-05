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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.ConsoleApp;

internal sealed class InteractiveResearchPlanner
{
    private static readonly JsonNode PlannerResponseSchema = JsonNode.Parse(
        """
        {
            "type": "object",
            "required": ["action", "summary", "keyQuestions"],
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

    public InteractiveResearchPlanner(
        IAzureOpenAiService openAiService,
        string chatDeploymentName,
        ILogger<InteractiveResearchPlanner>? logger = null)
    {
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _chatDeploymentName = string.IsNullOrWhiteSpace(chatDeploymentName)
            ? throw new ArgumentException("Chat deployment name must be provided.", nameof(chatDeploymentName))
            : chatDeploymentName;
        _logger = logger ?? NullLogger<InteractiveResearchPlanner>.Instance;
    }

    public async Task<PlannerOutcome?> RunAsync(string objective, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            throw new ArgumentException("Objective must be provided.", nameof(objective));
        }

        string currentObjective = objective.Trim();
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
                    Console.WriteLine();
                    Console.WriteLine("# Planner Clarification");
                    Console.WriteLine(string.IsNullOrWhiteSpace(response.FollowUp)
                        ? "The planner needs more details before creating a plan."
                        : response.FollowUp);
                    Console.Write("Your answer (leave blank to cancel): ");
                    string? clarification = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
                    if (string.IsNullOrWhiteSpace(clarification))
                    {
                        Console.WriteLine("Planner cancelled. Returning to menu.");
                        return null;
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
                            return null;
                        default:
                            Console.WriteLine("Unrecognized action. Please choose C, R, E, or A.");
                            continue;
                    }

                case PlannerAction.Abort:
                    Console.WriteLine("Planner aborted due to insufficient detail.");
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
            throw new InvalidOperationException("Planner could not produce a plan for the provided objective.");
        }

        return new PlannerOutcome(trimmedObjective, response.Summary, response.KeyQuestions, BuildContextHints(response));
    }

    private async Task<PlannerResponse> InvokePlannerAsync(List<OpenAiChatMessage> history, CancellationToken cancellationToken)
    {
        var request = new OpenAiChatRequest(
            SystemPrompts.Planner.Coordinator,
            history,
            _chatDeploymentName,
            JsonSchemaFormat: new OpenAiChatJsonSchemaFormat("planner_response", PlannerResponseSchema));

        string response = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Planner response payload: {Payload}", response);
        return PlannerResponse.Parse(response);
    }

    private static IReadOnlyList<string> BuildContextHints(PlannerResponse response)
    {
        var hints = new List<string>
        {
            $"PlannerSummary: {response.Summary}"
        };

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

    private static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => await Task.Run(Console.ReadLine, cancellationToken).ConfigureAwait(false);

    private enum PlannerAction
    {
        RequestClarification,
        PlanReady,
        Abort
    }

    private sealed record PlannerResponse(PlannerAction Action, string Summary, IReadOnlyList<string> KeyQuestions, string? FollowUp, string? Notes)
    {
        public static PlannerResponse Parse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new PlannerResponse(PlannerAction.Abort, string.Empty, Array.Empty<string>(), null, null);
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

                string? followUp = root.TryGetProperty("followUp", out var followUpElement) && followUpElement.ValueKind == JsonValueKind.String
                    ? followUpElement.GetString()
                    : null;
                string? notes = root.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.String
                    ? notesElement.GetString()
                    : null;

                return action switch
                {
                    "ask" => new PlannerResponse(PlannerAction.RequestClarification, summary, keyQuestions, followUp, notes),
                    "plan" => new PlannerResponse(PlannerAction.PlanReady, summary, keyQuestions, followUp, notes),
                    "abort" => new PlannerResponse(PlannerAction.Abort, summary, keyQuestions, followUp, notes),
                    _ => new PlannerResponse(PlannerAction.PlanReady, summary, keyQuestions, followUp, notes)
                };
            }
            catch (JsonException)
            {
                return new PlannerResponse(PlannerAction.Abort, string.Empty, Array.Empty<string>(), null, null);
            }
        }
    }
}

internal sealed record PlannerOutcome(
    string Objective,
    string PlanSummary,
    IReadOnlyList<string> KeyQuestions,
    IReadOnlyList<string> ContextHints);