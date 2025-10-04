using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Marconian.ResearchAgent.Agents;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.ComputerUse;
using Marconian.ResearchAgent.Services.Cosmos;
using Marconian.ResearchAgent.Services.Files;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Tools;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using System.Collections.ObjectModel;
using Marconian.ResearchAgent.Logging;
using Marconian.ResearchAgent.Tracking;

namespace Marconian.ResearchAgent;

internal static class Program
{
    private const string SessionRecordType = "session_metadata";

    private sealed record SessionInfo(string SessionId, string Objective, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, MemoryRecord Record);

    private static async Task<int> Main(string[] args)
    {
        bool cosmosDiagnostics = args.Any(argument => string.Equals(argument, "--cosmos-diagnostics", StringComparison.OrdinalIgnoreCase));
        string defaultReportDirectory;
        string fileLoggerPath = Path.Combine(Directory.GetCurrentDirectory(), "debug", "marconian.log");

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("Marconian.ResearchAgent", LogLevel.Debug);
            builder.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "HH:mm:ss ";
                options.SingleLine = true;
            });
            builder.AddFileLogger(fileLoggerPath, LogLevel.Debug);
        });

        ILogger logger = loggerFactory.CreateLogger("Marconian");

        UnhandledExceptionEventHandler unhandledExceptionHandler = (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
            {
                logger.LogCritical(ex, "Unhandled exception encountered. Application will terminate.");
            }
        };
        AppDomain.CurrentDomain.UnhandledException += unhandledExceptionHandler;

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!cts.IsCancellationRequested)
            {
                logger.LogWarning("Cancellation requested. Attempting graceful shutdown...");
                cts.Cancel();
            }
        };
        Console.CancelKeyPress += cancelHandler;

        ComputerUseSearchService? computerUseSearchService = null;
        string? computerUseDisabledReason = null;

        ResearchFlowTracker? flowTracker = null;
        try
        {

            Settings.AppSettings settings;
            try
            {
                settings = Settings.LoadAndValidate();
                defaultReportDirectory = settings.ReportsDirectory;
                Directory.CreateDirectory(defaultReportDirectory);
                logger.LogInformation("Configuration loaded successfully.");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "Configuration validation failed.");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            if (cosmosDiagnostics)
            {
                await RunCosmosDiagnosticsAsync(settings, loggerFactory, logger, cts.Token).ConfigureAwait(false);
                return 0;
            }

            logger.LogInformation("Initializing Cosmos DB client.");
            await using var cosmosService = new CosmosMemoryService(settings, logger: loggerFactory.CreateLogger<CosmosMemoryService>());
            await cosmosService.InitializeAsync(cts.Token).ConfigureAwait(false);

            logger.LogInformation("Initializing hybrid cache (memory + disk).");
            await using var cacheService = new HybridCacheService(settings.CacheDirectory, loggerFactory.CreateLogger<HybridCacheService>());

            flowTracker = new ResearchFlowTracker(loggerFactory.CreateLogger<ResearchFlowTracker>());

            var openAiService = new AzureOpenAiService(settings, loggerFactory.CreateLogger<AzureOpenAiService>());
            var longTermMemory = new LongTermMemoryManager(
                cosmosService,
                openAiService,
                settings.AzureOpenAiEmbeddingDeployment,
                loggerFactory.CreateLogger<LongTermMemoryManager>());
            var documentService = new DocumentIntelligenceService(settings, loggerFactory.CreateLogger<DocumentIntelligenceService>());
            var fileRegistryService = new FileRegistryService();
            using var sharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            if (settings.WebSearchProvider == WebSearchProvider.ComputerUse && string.IsNullOrWhiteSpace(settings.AzureOpenAiComputerUseDeployment))
            {
                throw new InvalidOperationException("AZURE_OPENAI_COMPUTER_USE_DEPLOYMENT must be set when using the computer-use search provider.");
            }

            bool computerUseConfigured = settings.ComputerUseEnabled && !string.IsNullOrWhiteSpace(settings.AzureOpenAiComputerUseDeployment);

            if (computerUseConfigured)
            {
                var probeLogger = loggerFactory.CreateLogger("ComputerUseProbe");
                ComputerUseReadinessResult readiness = await ComputerUseRuntimeProbe
                    .EnsureReadyAsync(probeLogger, cts.Token)
                    .ConfigureAwait(false);

                if (readiness.IsReady)
                {
                    computerUseSearchService = new ComputerUseSearchService(
                        settings.AzureOpenAiEndpoint,
                        settings.AzureOpenAiApiKey,
                        settings.AzureOpenAiComputerUseDeployment!,
                        sharedHttpClient,
                        loggerFactory.CreateLogger<ComputerUseSearchService>());
                    logger.LogInformation(
                        "Computer-use automation is ready (Mode={Mode}, Provider={Provider}).",
                        settings.ComputerUseMode,
                        settings.WebSearchProvider);
                }
                else
                {
                    computerUseDisabledReason = readiness.FailureReason ?? "Playwright browsers are unavailable.";
                }
            }
            else if (!settings.ComputerUseEnabled)
            {
                computerUseDisabledReason = "Computer-use automation disabled via COMPUTER_USE_ENABLED=false.";
            }
            else if (string.IsNullOrWhiteSpace(settings.AzureOpenAiComputerUseDeployment))
            {
                computerUseDisabledReason = "AZURE_OPENAI_COMPUTER_USE_DEPLOYMENT not configured.";
            }

            if (computerUseDisabledReason is not null)
            {
                if (settings.WebSearchProvider == WebSearchProvider.ComputerUse && settings.ComputerUseMode == ComputerUseMode.Full)
                {
                    logger.LogError("Computer-use mode is set to Full but the runtime is unavailable: {Reason}", computerUseDisabledReason);
                    Console.Error.WriteLine($"Computer-use provider unavailable: {computerUseDisabledReason}");
                    return 1;
                }

                if (settings.WebSearchProvider == WebSearchProvider.ComputerUse)
                {
                    logger.LogWarning("Computer-use search disabled: {Reason}. Falling back to Google API provider where possible.", computerUseDisabledReason);
                }
                else
                {
                    logger.LogWarning("Computer-use navigation unavailable: {Reason}.", computerUseDisabledReason);
                }
            }
            if (settings.WebSearchProvider == WebSearchProvider.GoogleApi)
            {
                logger.LogInformation(
                    computerUseSearchService is not null
                        ? "Using Google API web search provider with computer-use navigation enabled."
                        : "Using Google API web search provider.");
            }
            bool googleCredentialsAvailable =
                !string.IsNullOrWhiteSpace(settings.GoogleApiKey) && !string.IsNullOrWhiteSpace(settings.GoogleSearchEngineId);

            WebSearchProvider fallbackProvider =
                settings.WebSearchProvider == WebSearchProvider.ComputerUse &&
                settings.ComputerUseMode == ComputerUseMode.Hybrid &&
                googleCredentialsAvailable
                    ? WebSearchProvider.GoogleApi
                    : WebSearchProvider.ComputerUse;

            Func<IEnumerable<ITool>> toolFactory = () =>
            {
                var tools = new List<ITool>
                {
                    new WebSearchTool(
                        settings.GoogleApiKey,
                        settings.GoogleSearchEngineId,
                        cacheService,
                        sharedHttpClient,
                        loggerFactory.CreateLogger<WebSearchTool>(),
                        settings.WebSearchProvider,
                        computerUseSearchService,
                        fallbackProvider: fallbackProvider,
                        computerUseDisabledReason: computerUseDisabledReason,
                        computerUseMode: settings.ComputerUseMode),
                    new WebScraperTool(cacheService, sharedHttpClient, loggerFactory.CreateLogger<WebScraperTool>()),
                    new FileReaderTool(fileRegistryService, documentService, sharedHttpClient, loggerFactory.CreateLogger<FileReaderTool>()),
                    new ImageReaderTool(fileRegistryService, openAiService, settings.AzureOpenAiVisionDeployment, sharedHttpClient, loggerFactory.CreateLogger<ImageReaderTool>())
                };

                if (computerUseSearchService is not null)
                {
                    tools.Add(new ComputerUseNavigatorTool(
                        computerUseSearchService,
                        loggerFactory.CreateLogger<ComputerUseNavigatorTool>()));
                }

                return tools;
            };

            ParseCommandLine(
                args,
                out string? resumeSessionId,
                out string? providedQuery,
                out string? reportDirectoryArgument,
                out string? dumpSessionId,
                out string? dumpDirectory,
                out string? dumpType,
                out int dumpLimit,
                out string? diagnoseComputerUseArgument,
                out string? reportOnlySessionId);

            bool reportOnlyMode = !string.IsNullOrWhiteSpace(reportOnlySessionId);
            if (reportOnlyMode)
            {
                if (!string.IsNullOrWhiteSpace(dumpSessionId) || !string.IsNullOrWhiteSpace(diagnoseComputerUseArgument))
                {
                    logger.LogError("--report-session cannot be combined with dump or diagnostic operations.");
                    Console.Error.WriteLine("--report-session cannot be combined with dump or diagnostic operations.");
                    return 1;
                }

                if (!string.IsNullOrWhiteSpace(resumeSessionId) &&
                    !string.Equals(resumeSessionId, reportOnlySessionId, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Ignoring --resume value {ResumeId} in favor of --report-session {ReportId}.", resumeSessionId, reportOnlySessionId);
                }

                resumeSessionId = reportOnlySessionId;
                providedQuery = null;
            }

            if (!string.IsNullOrWhiteSpace(dumpSessionId))
            {
                string destination = string.IsNullOrWhiteSpace(dumpDirectory)
                    ? Path.Combine(defaultReportDirectory, "session_dumps", dumpSessionId)
                    : Path.GetFullPath(dumpDirectory);

                Directory.CreateDirectory(destination);

                var typeFilters = ParseDumpTypes(dumpType);
                await DumpSessionArtifactsAsync(
                        cosmosService,
                        dumpSessionId,
                        typeFilters,
                        destination,
                        dumpLimit,
                        loggerFactory.CreateLogger("SessionDump"),
                        cts.Token)
                    .ConfigureAwait(false);

                logger.LogInformation("Session dump complete for {SessionId}. Files written to {Destination}.", dumpSessionId, destination);
                return 0;
            }

            string reportDirectory = string.IsNullOrWhiteSpace(reportDirectoryArgument)
                ? defaultReportDirectory
                : Path.GetFullPath(reportDirectoryArgument);
            Directory.CreateDirectory(reportDirectory);

            if (!string.IsNullOrWhiteSpace(diagnoseComputerUseArgument))
            {
                if (computerUseSearchService is null)
                {
                    string reason = computerUseDisabledReason ?? "Computer-use automation is not available in the current configuration.";
                    logger.LogError("Cannot run computer-use diagnosis: {Reason}", reason);
                    Console.Error.WriteLine($"Cannot run computer-use diagnosis: {reason}");
                    return 1;
                }

                string trimmed = diagnoseComputerUseArgument.Trim();
                string[] parts = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
                string url = parts.Length > 0 ? parts[0] : string.Empty;
                string? objective = parts.Length > 1 ? parts[1] : null;

                if (string.IsNullOrWhiteSpace(url))
                {
                    logger.LogError("Invalid --diagnose-computer-use argument. Provide a URL followed by an optional objective separated by a pipe (|).");
                    Console.Error.WriteLine("Invalid --diagnose-computer-use argument. Expected format: \"https://example.com|optional objective\"");
                    return 1;
                }

                Console.WriteLine($"Running computer-use diagnosis for: {url}");
                if (!string.IsNullOrWhiteSpace(objective))
                {
                    Console.WriteLine($"Objective: {objective}");
                }

                try
                {
                    ComputerUseExplorationResult exploration = await computerUseSearchService
                        .ExploreAsync(url, objective, cts.Token)
                        .ConfigureAwait(false);

                    Console.WriteLine();
                    Console.WriteLine("# Computer-use Exploration Summary");
                    Console.WriteLine($"Requested URL : {exploration.RequestedUrl}");
                    if (!string.IsNullOrWhiteSpace(exploration.FinalUrl))
                    {
                        Console.WriteLine($"Final URL      : {exploration.FinalUrl}");
                    }

                    if (!string.IsNullOrWhiteSpace(exploration.PageTitle))
                    {
                        Console.WriteLine($"Page Title     : {exploration.PageTitle}");
                    }

                    Console.WriteLine();
                    string summary = BuildExplorationSummary(exploration);
                    Console.WriteLine(summary);

                    if (exploration.FlaggedResources.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Flagged Resources:");
                        foreach (FlaggedResource resource in exploration.FlaggedResources)
                        {
                            Console.WriteLine($" - [{resource.Type}] {resource.Title ?? resource.Url}");
                            if (!string.IsNullOrWhiteSpace(resource.Url))
                            {
                                Console.WriteLine($"   URL: {resource.Url}");
                            }
                            if (!string.IsNullOrWhiteSpace(resource.Notes))
                            {
                                Console.WriteLine($"   Notes: {resource.Notes}");
                            }
                        }
                    }

                    if (exploration.Transcript.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Recent Transcript:");
                        foreach (string segment in exploration.Transcript.TakeLast(5))
                        {
                            if (!string.IsNullOrWhiteSpace(segment))
                            {
                                Console.WriteLine($" - {segment.Trim()}");
                            }
                        }
                    }

                    Console.WriteLine();
                    Console.WriteLine("Diagnosis complete. Detailed timeline saved under debug/computer-use.");
                    return 0;
                }
                catch (ComputerUseSearchBlockedException blocked)
                {
                    logger.LogError(blocked, "Computer-use diagnosis blocked for {Url}", url);
                    Console.Error.WriteLine($"Computer-use exploration blocked: {blocked.Message}");
                    return 1;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Computer-use diagnosis failed for {Url}", url);
                    Console.Error.WriteLine($"Computer-use exploration failed: {ex.Message}");
                    return 1;
                }
            }

            var orchestrator = new OrchestratorAgent(
                openAiService,
                longTermMemory,
                toolFactory,
                settings.AzureOpenAiChatDeployment,
                loggerFactory.CreateLogger<OrchestratorAgent>(),
                loggerFactory,
                cacheService,
                reportsDirectory: reportDirectory);

            logger.LogInformation("Loading existing research sessions from Cosmos DB.");
            var existingSessionRecords = await cosmosService.QueryByTypeAsync(SessionRecordType, 100, cts.Token).ConfigureAwait(false);
            var sessionLookup = existingSessionRecords.ToDictionary(record => record.ResearchSessionId, StringComparer.OrdinalIgnoreCase);
            var sessionInfos = existingSessionRecords.Select(MapSession).OrderByDescending(info => info.UpdatedAtUtc).ToList();
            logger.LogInformation("{SessionCount} stored sessions loaded.", sessionInfos.Count);

            SessionInfo? sessionToResume = null;
            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                if (sessionLookup.TryGetValue(resumeSessionId!, out var existingRecord))
                {
                    sessionToResume = MapSession(existingRecord);
                    logger.LogInformation("Resuming session {SessionId}.", resumeSessionId);
                }
                else
                {
                    logger.LogError("Requested session {SessionId} not found.", resumeSessionId);
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
                logger.LogError("No research objective provided. Exiting.");
                Console.Error.WriteLine("No research objective provided. Exiting.");
                return 1;
            }

            researchObjective = researchObjective.Trim();
            if (researchObjective.Length == 0)
            {
                logger.LogError("Empty research objective after trimming input. Exiting.");
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
                logger.LogInformation("Created new session {SessionId}.", sessionId);
            }
            else
            {
                sessionId = sessionToResume.SessionId;
                sessionRecord = sessionToResume.Record;
                logger.LogInformation("Continuing session {SessionId}.", sessionId);
            }

            var task = new AgentTask
            {
                TaskId = sessionId,
                ResearchSessionId = sessionId,
                Objective = researchObjective,
                Parameters = new Dictionary<string, string>(),
                ContextHints = new List<string>()
            };

            if (reportOnlyMode)
            {
                task.Parameters["reportOnly"] = "true";
            }

            string sessionMessage = sessionToResume is null
                ? $"Starting new research session '{sessionId}' for objective: {researchObjective}"
                : $"Resuming session '{sessionId}' (last status: {sessionToResume.Status}) for objective: {researchObjective}";

            if (reportOnlyMode)
            {
                string status = sessionToResume?.Status ?? "unknown";
                sessionMessage = $"Regenerating report for session '{sessionId}' (previous status: {status}) using stored findings.";
                logger.LogInformation("Regenerating report for session {SessionId} using stored findings.", sessionId);
            }

            Console.WriteLine(sessionMessage);

            if (reportOnlyMode)
            {
                logger.LogInformation("Regenerating report for session {SessionId} without new exploration.", sessionId);
            }
            else
            {
                logger.LogInformation("Executing research objective '{Objective}' for session {SessionId}.", researchObjective, sessionId);
            }

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
                    logger.LogError("Execution error: {Error}", error);
                }
            }

            logger.LogInformation("Research session {SessionId} finished with success={Success}.", sessionId, executionResult.Success);
            return executionResult.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Execution cancelled by request.");
            Console.Error.WriteLine("Operation cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error executing research workflow.");
            Console.Error.WriteLine("Fatal error encountered. Check logs for details.");
            return 1;
        }

        finally
        {
            if (computerUseSearchService is not null)
            {
                await computerUseSearchService.DisposeAsync().ConfigureAwait(false);
            }

            if (flowTracker is not null)
            {
                await flowTracker.SaveAsync().ConfigureAwait(false);
            }

            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.UnhandledException -= unhandledExceptionHandler;
        }
    }


    private static IReadOnlyList<string> ParseDumpTypes(string? dumpType)
    {
        if (string.IsNullOrWhiteSpace(dumpType))
        {
            return Array.Empty<string>();
        }

        string trimmed = dumpType.Trim();
        if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase) || trimmed == "*")
        {
            return Array.Empty<string>();
        }

        string[] parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? Array.Empty<string>() : parts;
    }

    private static async Task DumpSessionArtifactsAsync(
        CosmosMemoryService cosmosService,
        string sessionId,
        IReadOnlyList<string> typeFilters,
        string outputDirectory,
        int limit,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        HashSet<string>? filterSet = typeFilters.Count == 0
            ? null
            : new HashSet<string>(typeFilters, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<MemoryRecord> records = await cosmosService
            .QueryBySessionAsync(sessionId, Math.Max(limit, 1), cancellationToken)
            .ConfigureAwait(false);

        var filtered = records
            .Where(record => filterSet is null || filterSet.Contains(record.Type))
            .OrderByDescending(record => record.CreatedAtUtc)
            .Take(limit)
            .ToList();

        if (filtered.Count == 0)
        {
            logger.LogWarning("No memory records matched the export criteria for session {SessionId}.", sessionId);
            Console.WriteLine("# Session Dump\nNo records matched the export criteria.");
            return;
        }

        logger.LogInformation(
            "Exporting {Count} memory records (limit {Limit}) from session {SessionId} to {OutputDirectory}.",
            filtered.Count,
            limit,
            sessionId,
            outputDirectory);

        var indexEntries = new List<object>(filtered.Count);
        int counter = 1;
        foreach (var record in filtered)
        {
            string fileName = $"{counter:000}_{SanitizeFileName(record.Type)}_{SanitizeFileName(record.Id)}.md";
            string filePath = Path.Combine(outputDirectory, fileName);

            var builder = new StringBuilder();
            builder.AppendLine($"# Memory Record {counter}");
            builder.AppendLine($"- RecordId: {record.Id}");
            builder.AppendLine($"- Type: {record.Type}");
            builder.AppendLine($"- CreatedUtc: {record.CreatedAtUtc:O}");

            if (record.Metadata.Count > 0)
            {
                builder.AppendLine("- Metadata:");
                foreach (var kvp in record.Metadata)
                {
                    builder.AppendLine($"    - {kvp.Key}: {kvp.Value}");
                }
            }

            if (record.Sources.Count > 0)
            {
                builder.AppendLine("- Sources:");
                foreach (var source in record.Sources)
                {
                    builder.AppendLine($"    - {source.Title ?? source.SourceId}: {source.Url}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("## Content");
            builder.AppendLine();
            builder.AppendLine(record.Content);

            await File.WriteAllTextAsync(filePath, builder.ToString(), cancellationToken).ConfigureAwait(false);

            indexEntries.Add(new
            {
                record.Id,
                record.Type,
                record.CreatedAtUtc,
                record.Metadata,
                record.Sources,
                ContentFile = fileName
            });

            counter++;
        }

        string indexPath = Path.Combine(outputDirectory, "index.json");
        await File.WriteAllTextAsync(
                indexPath,
                JsonSerializer.Serialize(indexEntries, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"# Session Dump\nExported {filtered.Count} record(s) to {outputDirectory}.\nIndex: {indexPath}");
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "record";
        }

        var builder = new StringBuilder(value.Length);
        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (char ch in value)
        {
            if (Array.IndexOf(invalid, ch) >= 0)
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        string sanitized = builder.ToString().Trim('_');
        if (sanitized.Length == 0)
        {
            sanitized = "record";
        }

        return sanitized.Length > 64 ? sanitized[..64] : sanitized;
    }

    private static void ParseCommandLine(
        string[] args,
        out string? resumeSessionId,
        out string? query,
        out string? reportsDirectory,
        out string? dumpSessionId,
        out string? dumpDirectory,
        out string? dumpType,
        out int dumpLimit,
        out string? diagnoseComputerUse,
        out string? reportSessionId)
    {
        resumeSessionId = null;
        query = null;
        reportsDirectory = null;
        dumpSessionId = null;
        dumpDirectory = null;
        dumpType = null;
        dumpLimit = 200;
        diagnoseComputerUse = null;
        reportSessionId = null;

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
                case "--reports":
                    if (i + 1 < args.Length)
                    {
                        reportsDirectory = args[++i];
                    }
                    break;
                case "--dump-session":
                    if (i + 1 < args.Length)
                    {
                        dumpSessionId = args[++i];
                    }
                    break;
                case "--dump-dir":
                case "--dump-output":
                    if (i + 1 < args.Length)
                    {
                        dumpDirectory = args[++i];
                    }
                    break;
                case "--dump-type":
                    if (i + 1 < args.Length)
                    {
                        dumpType = args[++i];
                    }
                    break;
                case "--dump-limit":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int parsedLimit) && parsedLimit > 0)
                    {
                        dumpLimit = parsedLimit;
                    }
                    break;
                case "--diagnose-computer-use":
                    if (i + 1 < args.Length)
                    {
                        diagnoseComputerUse = args[++i];
                    }
                    break;
                case "--report-session":
                    if (i + 1 < args.Length)
                    {
                        reportSessionId = args[++i];
                    }
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("Usage: dotnet run [--query \"question\"] [--resume sessionId] [--reports path] [--dump-session sessionId [--dump-type type] [--dump-dir path] [--dump-limit N]] [--diagnose-computer-use \"url|objective\"] [--report-session sessionId]");
                    Environment.Exit(0);
                    break;
            }
        }
    }

    private static string BuildExplorationSummary(ComputerUseExplorationResult exploration)
    {
        if (!string.IsNullOrWhiteSpace(exploration.StructuredSummaryJson))
        {
            return exploration.StructuredSummaryJson!.Trim();
        }

        if (exploration.Transcript.Count == 0)
        {
            return "No summary was produced by the exploration session.";
        }

        var builder = new StringBuilder();
        foreach (string segment in exploration.Transcript.TakeLast(3))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(segment.Trim());
        }

        return builder.Length == 0
            ? "No summary was produced by the exploration session."
            : builder.ToString();
    }
    private static async Task RunCosmosDiagnosticsAsync(
        Settings.AppSettings settings,
        ILoggerFactory loggerFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Running Cosmos DB diagnostics against existing container.");

        var clientOptions = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            EnableContentResponseOnWrite = true
        };

        using var client = new CosmosClient(settings.CosmosConnectionString, clientOptions);

        Database database = await client.CreateDatabaseIfNotExistsAsync("MarconianMemory", cancellationToken: cancellationToken).ConfigureAwait(false);

        var containerProperties = new ContainerProperties("MemoryRecordsV2", "/researchSessionId")
        {
            IndexingPolicy = new IndexingPolicy
            {
                Automatic = true,
                IndexingMode = IndexingMode.Consistent
            }
        };

        containerProperties.IndexingPolicy.IncludedPaths.Clear();
        containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        containerProperties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = "/embedding",
            Type = VectorIndexType.DiskANN
        });

        containerProperties.VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Embedding>
        {
            new Embedding
            {
                Path = "/embedding",
                Dimensions = 3072,
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine
            }
        });

        ContainerResponse containerResponse = await database
            .CreateContainerIfNotExistsAsync(containerProperties, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Container container = containerResponse.Container;

        var vectorPolicy = containerResponse.Resource.VectorEmbeddingPolicy;
        if (vectorPolicy is null || vectorPolicy.Embeddings.Count == 0)
        {
            logger.LogWarning("Vector embedding policy is not configured on container {ContainerId}.", container.Id);
        }
        else
        {
            foreach (var embedding in vectorPolicy.Embeddings)
            {
                logger.LogInformation(
                    "Container {ContainerId} vector path {Path} configured for {Dimensions} dimensions (type {DataType}, distance {DistanceFunction}).",
                    container.Id,
                    embedding.Path,
                    embedding.Dimensions,
                    embedding.DataType,
                    embedding.DistanceFunction);
            }
        }

        if (containerResponse.Resource.UniqueKeyPolicy?.UniqueKeys is { Count: > 0 } uniqueKeys)
        {
            foreach (var uniqueKey in uniqueKeys)
            {
                logger.LogInformation(
                    "Container {ContainerId} unique key on paths: {Paths}.",
                    container.Id,
                    string.Join(", ", uniqueKey.Paths));
            }
        }
        else
        {
            logger.LogInformation("Container {ContainerId} has no unique key policy configured.", container.Id);
        }

        int containerEmbeddingDimensions = vectorPolicy?
            .Embeddings
            .FirstOrDefault(static embedding => string.Equals(embedding.Path, "/embedding", StringComparison.OrdinalIgnoreCase))?
            .Dimensions ?? 0;

        async Task UpsertProbeAsync(string label, Dictionary<string, object?> document, string partitionKey)
        {
            try
            {
                string payload = JsonSerializer.Serialize(document);
                logger.LogInformation("[{Label}] Upserting document: {Payload}", label, payload);
                await container.UpsertItemAsync(document, new PartitionKey(partitionKey), cancellationToken: cancellationToken).ConfigureAwait(false);
                logger.LogInformation("[{Label}] Upsert succeeded.", label);
            }
            catch (CosmosException ex)
            {
                logger.LogWarning(ex, "[{Label}] Upsert failed.", label);
            }
        }

        async Task QueryPartitionAsync(string label, string partitionKey)
        {
            try
            {
                var query = new QueryDefinition("SELECT * FROM c WHERE c.researchSessionId = @partition")
                    .WithParameter("@partition", partitionKey);

                using FeedIterator<dynamic> iterator = container.GetItemQueryIterator<dynamic>(
                    query,
                    requestOptions: new QueryRequestOptions
                    {
                        PartitionKey = new PartitionKey(partitionKey)
                    });

                int count = 0;
                while (iterator.HasMoreResults)
                {
                    FeedResponse<dynamic> response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var item in response)
                    {
                        count++;
                        string json = JsonSerializer.Serialize(item);
                        logger.LogInformation("[{Label}] Retrieved document: {Document}", label, json);
                    }
                }

                logger.LogInformation("[{Label}] Total documents retrieved: {Count}.", label, count);
            }
            catch (CosmosException ex)
            {
                logger.LogWarning(ex, "[{Label}] Query failed.", label);
            }
        }

        async Task UpsertMemoryRecordProbeAsync(string label, MemoryRecord record)
        {
            string payload = JsonSerializer.Serialize(record);
            string snippet = payload.Length > 512 ? payload[..512] + "..." : payload;
            Dictionary<string, object?> document = ConvertRecord(record);

            try
            {
                logger.LogInformation(
                    "[{Label}] Upserting typed MemoryRecord with embedding length {EmbeddingLength}. Payload: {Payload}",
                    label,
                    record.Embedding?.Length ?? 0,
                    snippet);

                await container
                    .UpsertItemAsync(document, new PartitionKey(record.ResearchSessionId), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation("[{Label}] Typed upsert succeeded.", label);
            }
            catch (CosmosException ex)
            {
                logger.LogWarning(
                    ex,
                    "[{Label}] Typed upsert failed. Response: {ResponseBody}",
                    label,
                    ex.ResponseBody);
            }
        }

        string partition = $"diagnostic_{Guid.NewGuid():N}";

        Dictionary<string, object?> ConvertRecord(MemoryRecord record)
        {
            var document = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = record.Id,
                ["researchSessionId"] = record.ResearchSessionId,
                ["type"] = record.Type,
                ["content"] = record.Content,
                ["createdAtUtc"] = record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["metadata"] = new Dictionary<string, string>(record.Metadata, StringComparer.Ordinal),
                ["sources"] = record.Sources.Count == 0
                    ? Array.Empty<Dictionary<string, string?>>()
                    : record.Sources
                        .Select(source => new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["sourceId"] = source.SourceId,
                            ["title"] = source.Title,
                            ["url"] = source.Url,
                            ["snippet"] = source.Snippet
                        })
                        .ToArray()
            };

            if (record.Embedding is { Length: > 0 } embedding)
            {
                document["embedding"] = NormalizeEmbedding(embedding, record.Id);
            }

            return document;
        }

        float[] NormalizeEmbedding(IReadOnlyList<float> embedding, string label)
        {
            int expected = Math.Max(1, containerEmbeddingDimensions);
            if (embedding.Count == expected && embedding is float[] floatArray)
            {
                return floatArray;
            }

            var buffer = new float[expected];
            int toCopy = Math.Min(expected, embedding.Count);
            for (int index = 0; index < toCopy; index++)
            {
                buffer[index] = embedding[index];
            }

            if (embedding.Count != expected)
            {
                logger.LogWarning(
                    "[{Label}] Embedding length mismatch. Actual={Actual}, Expected={Expected}. Vector will be padded or truncated.",
                    label,
                    embedding.Count,
                    expected);
            }

            return buffer;
        }

        Dictionary<string, object?> CreateDocument(string label, string content, float[]? embedding = null)
        {
            var doc = new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["researchSessionId"] = partition,
                ["type"] = label,
                ["content"] = content,
                ["metadata"] = new Dictionary<string, string>(),
                ["sources"] = Array.Empty<object>(),
                ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            if (embedding is not null)
            {
                doc["embedding"] = embedding;
            }

            return doc;
        }

        await UpsertProbeAsync("minimal", CreateDocument("diagnostic_minimal", "Minimal document"), partition).ConfigureAwait(false);
        await QueryPartitionAsync("minimal", partition).ConfigureAwait(false);

        await UpsertProbeAsync(
            "embedding_small",
            CreateDocument("diagnostic_embedding_small", "Small embedding", new[] { 0.1f, 0.2f, 0.3f }),
            partition).ConfigureAwait(false);
        await QueryPartitionAsync("embedding_small", partition).ConfigureAwait(false);

        await UpsertProbeAsync(
            "embedding_large",
            CreateDocument("diagnostic_embedding_large", "Large embedding", Enumerable.Repeat(0f, 3072).ToArray()),
            partition).ConfigureAwait(false);
        await QueryPartitionAsync("embedding_large", partition).ConfigureAwait(false);

        var sessionStateDictionary = CreateDocument(
            "session_state",
            "Session state diagnostic (dictionary)",
            Enumerable.Repeat(0f, 3072).ToArray());
        sessionStateDictionary["id"] = $"{partition}_session_state_dictionary";
        sessionStateDictionary["metadata"] = new Dictionary<string, string>
        {
            ["state"] = "diagnostic",
            ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        await UpsertProbeAsync("session_state_dictionary", sessionStateDictionary, partition).ConfigureAwait(false);
        await QueryPartitionAsync("session_state_dictionary", partition).ConfigureAwait(false);

        int typedDimensions = containerEmbeddingDimensions > 0 ? containerEmbeddingDimensions : 0;
        if (typedDimensions == 0)
        {
            logger.LogWarning("Container {ContainerId} did not report embedding dimensions; diagnostics will omit the embedding vector.", container.Id);
        }
        float[]? typedEmbedding = typedDimensions > 0
            ? Enumerable.Repeat(0f, typedDimensions).ToArray()
            : null;

        var typedSessionRecord = new MemoryRecord
        {
            Id = $"{partition}_session_state",
            ResearchSessionId = partition,
            Type = "session_state",
            Content = "Typed session state diagnostic",
            Embedding = typedEmbedding,
            Metadata = new Dictionary<string, string>
            {
                ["state"] = "diagnostic",
                ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }
        };

        await UpsertMemoryRecordProbeAsync("session_state_typed", typedSessionRecord).ConfigureAwait(false);
        await QueryPartitionAsync("session_state_typed", partition).ConfigureAwait(false);

        var typedWithoutEmbedding = new MemoryRecord
        {
            Id = $"{partition}_session_state_no_embedding",
            ResearchSessionId = partition,
            Type = "session_state",
            Content = "Typed session state diagnostic",
            Embedding = null,
            Metadata = new Dictionary<string, string>(typedSessionRecord.Metadata)
        };

        await UpsertMemoryRecordProbeAsync("session_state_typed_no_embedding", typedWithoutEmbedding).ConfigureAwait(false);
        await QueryPartitionAsync("session_state_typed_no_embedding", partition).ConfigureAwait(false);
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





















