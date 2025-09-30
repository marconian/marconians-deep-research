using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using Marconian.ResearchAgent.Agents;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.Cosmos;
using Marconian.ResearchAgent.Services.Files;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Tools;

namespace Marconian.ResearchAgent;

internal static class Program
{
    private const string SessionRecordType = "session_metadata";

    private sealed record SessionInfo(string SessionId, string Objective, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, MemoryRecord Record);

    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!cts.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("Cancellation requested. Finishing current work...");
                cts.Cancel();
            }
        };

        Settings.AppSettings settings;
        try
        {
            settings = Settings.LoadAndValidate();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        await using var cosmosService = new CosmosMemoryService(settings);
        await cosmosService.InitializeAsync(cts.Token).ConfigureAwait(false);
        await using var redisCache = new RedisCacheService(settings);

        var openAiService = new AzureOpenAiService(settings);
        var longTermMemory = new LongTermMemoryManager(cosmosService, openAiService, settings.AzureOpenAiEmbeddingDeployment);
        var documentService = new DocumentIntelligenceService(settings);
        var fileRegistryService = new FileRegistryService();
        using var sharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        Func<IEnumerable<ITool>> toolFactory = () => new ITool[]
        {
            new WebSearchTool(settings.GoogleApiKey, settings.GoogleSearchEngineId, redisCache, sharedHttpClient),
            new WebScraperTool(redisCache, sharedHttpClient),
            new FileReaderTool(fileRegistryService, documentService, sharedHttpClient),
            new ImageReaderTool(fileRegistryService, openAiService, settings.AzureOpenAiVisionDeployment)
        };

        var orchestrator = new OrchestratorAgent(openAiService, longTermMemory, toolFactory, settings.AzureOpenAiChatDeployment);

        var existingSessionRecords = await cosmosService.QueryByTypeAsync(SessionRecordType, 100, cts.Token).ConfigureAwait(false);
        var sessionLookup = existingSessionRecords.ToDictionary(record => record.ResearchSessionId, StringComparer.OrdinalIgnoreCase);
        var sessionInfos = existingSessionRecords.Select(MapSession).OrderByDescending(info => info.UpdatedAtUtc).ToList();

        ParseCommandLine(args, out string? resumeSessionId, out string? providedQuery);

        SessionInfo? sessionToResume = null;
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            if (sessionLookup.TryGetValue(resumeSessionId!, out var existingRecord))
            {
                sessionToResume = MapSession(existingRecord);
            }
            else
            {
                Console.Error.WriteLine($"No session found with ID '{resumeSessionId}'.");
                return 1;
            }
        }

        string? researchObjective = providedQuery;
        if (sessionToResume is null && string.IsNullOrWhiteSpace(researchObjective))
        {
            var selection = await PromptForSessionSelectionAsync(sessionInfos, cts.Token).ConfigureAwait(false);
            sessionToResume = selection.SelectedSession;
            researchObjective = selection.Objective;
        }

        if (sessionToResume is not null)
        {
            researchObjective ??= sessionToResume.Objective;
        }

        researchObjective ??= settings.PrimaryResearchObjective;

        if (string.IsNullOrWhiteSpace(researchObjective))
        {
            Console.Error.WriteLine("No research objective provided. Exiting.");
            return 1;
        }

        researchObjective = researchObjective.Trim();
        if (researchObjective.Length == 0)
        {
            Console.Error.WriteLine("No research objective provided. Exiting.");
            return 1;
        }

        string sessionId;
        MemoryRecord sessionRecord;

        if (sessionToResume is null)
        {
            sessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            sessionRecord = new MemoryRecord
            {
                Id = sessionId,
                ResearchSessionId = sessionId,
                Type = SessionRecordType,
                Content = researchObjective,
                Metadata = new Dictionary<string, string>
                {
                    ["status"] = "in_progress",
                    ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                }
            };
        }
        else
        {
            sessionId = sessionToResume.SessionId;
            var metadata = new Dictionary<string, string>(sessionToResume.Record.Metadata)
            {
                ["status"] = "in_progress",
                ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            sessionRecord = new MemoryRecord
            {
                Id = sessionToResume.Record.Id,
                ResearchSessionId = sessionId,
                Type = SessionRecordType,
                Content = researchObjective,
                Metadata = metadata,
                Embedding = sessionToResume.Record.Embedding,
                Sources = sessionToResume.Record.Sources,
                CreatedAtUtc = sessionToResume.Record.CreatedAtUtc
            };
        }

        await cosmosService.UpsertRecordAsync(sessionRecord, cts.Token).ConfigureAwait(false);

        var task = new AgentTask
        {
            TaskId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ResearchSessionId = sessionId,
            Objective = researchObjective,
            Parameters = new Dictionary<string, string>(),
            ContextHints = new List<string>()
        };

        Console.WriteLine(sessionToResume is null
            ? $"Starting new research session '{sessionId}' for objective: {researchObjective}"
            : $"Resuming session '{sessionId}' (last status: {sessionToResume.Status}) for objective: {researchObjective}");

        AgentExecutionResult executionResult = await orchestrator.ExecuteTaskAsync(task, cts.Token).ConfigureAwait(false);

        sessionRecord.Metadata["status"] = executionResult.Success ? "completed" : "failed";
        sessionRecord.Metadata["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        sessionRecord.Metadata["lastSummary"] = executionResult.Summary;
        if (executionResult.Metadata.TryGetValue("reportPath", out var reportLocation) && !string.IsNullOrWhiteSpace(reportLocation))
        {
            sessionRecord.Metadata["lastReportPath"] = reportLocation;
        }

        await cosmosService.UpsertRecordAsync(sessionRecord, cts.Token).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(executionResult.Success ? "Research completed successfully." : "Research completed with errors.");
        Console.WriteLine(executionResult.Summary);
        if (executionResult.Metadata.TryGetValue("reportPath", out var finalReportPath) && !string.IsNullOrWhiteSpace(finalReportPath))
        {
            Console.WriteLine($"Report saved to: {finalReportPath}");
        }

        if (!executionResult.Success)
        {
            foreach (var error in executionResult.Errors)
            {
                Console.WriteLine($" - {error}");
            }
        }

        return executionResult.Success ? 0 : 1;
    }

    private static void ParseCommandLine(string[] args, out string? resumeSessionId, out string? query)
    {
        resumeSessionId = null;
        query = null;

        for (int i = 0; i < args.Length; i++)
        {
            string current = args[i];
            switch (current)
            {
                case "--resume":
                    if (i + 1 < args.Length)
                    {
                        resumeSessionId = args[++i];
                    }
                    break;
                case "--query":
                    if (i + 1 < args.Length)
                    {
                        query = args[++i];
                    }
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("Usage: dotnet run [--query \"question\"] [--resume sessionId]");
                    Environment.Exit(0);
                    break;
            }
        }
    }

    private static async Task<(SessionInfo? SelectedSession, string? Objective)> PromptForSessionSelectionAsync(IReadOnlyList<SessionInfo> sessions, CancellationToken token)
    {
        if (sessions.Count > 0)
        {
            Console.WriteLine("Existing research sessions:");
            for (int index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                Console.WriteLine($"[{index + 1}] {session.SessionId} | {session.Objective} | Status: {session.Status}");
            }

            Console.Write("Enter the number of a session to resume or press Enter to start a new session: ");
            string? choice = await ReadLineAsync(token).ConfigureAwait(false);
            if (int.TryParse(choice, out int selection) && selection >= 1 && selection <= sessions.Count)
            {
                var selected = sessions[selection - 1];
                return (selected, selected.Objective);
            }
        }

        Console.Write("Enter a new research objective: ");
        string? objective = (await ReadLineAsync(token).ConfigureAwait(false))?.Trim();
        return (null, objective);
    }

    private static async Task<string?> ReadLineAsync(CancellationToken token)
    {
        return await Task.Run(() => Console.ReadLine(), token).ConfigureAwait(false);
    }

    private static SessionInfo MapSession(MemoryRecord record)
    {
        var metadata = record.Metadata;
        string objective = record.Content;
        string status = metadata.TryGetValue("status", out var s) ? s : "unknown";
        DateTimeOffset created = ParseTimestamp(metadata, "createdAtUtc");
        DateTimeOffset updated = ParseTimestamp(metadata, "updatedAtUtc");
        return new SessionInfo(record.ResearchSessionId, objective, status, created, updated, record);
    }

    private static DateTimeOffset ParseTimestamp(Dictionary<string, string> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.MinValue;
    }
}
