using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Marconian.ResearchAgent.Agents;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.ConsoleApp;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Synthesis;
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
using Marconian.ResearchAgent.Utilities;

namespace Marconian.ResearchAgent;

internal static class Program
{
    private const string SessionRecordType = "session_metadata";

    private sealed record SessionInfo(string SessionId, string Objective, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, MemoryRecord Record);

    private enum MenuAction
    {
        StartNew,
        Resume,
        RegenerateReport,
        Exit
    }

    private sealed record MenuSelection(MenuAction Action, SessionInfo? SelectedSession, PlannerOutcome? PlannerOutcome);

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
    PooledComputerUseExplorer? computerUseExplorerPool = null;
    Func<ComputerUseSearchService>? computerUseServiceFactory = null;
        string? computerUseDisabledReason = null;

        ResearchFlowTracker? flowTracker = null;
        try
        {

            IConfigurationRoot configuration = Settings.BuildConfiguration();
            Settings.AppSettings settings;
            try
            {
                settings = Settings.LoadAndValidate(configuration);
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
                    computerUseServiceFactory = () => new ComputerUseSearchService(
                        settings.AzureOpenAiEndpoint,
                        settings.AzureOpenAiApiKey,
                        settings.AzureOpenAiComputerUseDeployment!,
                        sharedHttpClient,
                        loggerFactory.CreateLogger<ComputerUseSearchService>());

                    computerUseSearchService = computerUseServiceFactory();
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

            CommandLineOptions options = CommandLineOptions.Parse(args);

            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            string? resumeSessionId = options.ResumeSessionId;
            string? providedQuery = options.Query;
            string? reportDirectoryArgument = options.ReportsDirectory;
            string? dumpSessionId = options.DumpSessionId;
            string? dumpDirectory = options.DumpDirectory;
            string? dumpType = options.DumpType;
            int dumpLimit = options.DumpLimit;
            string? diagnoseComputerUseArgument = options.DiagnoseComputerUse;
            string? reportOnlySessionId = options.ReportSessionId;
            string? diagnosticsMode = options.DiagnosticsMode;

            bool otherOperationsRequested =
                !string.IsNullOrWhiteSpace(providedQuery) ||
                !string.IsNullOrWhiteSpace(resumeSessionId) ||
                !string.IsNullOrWhiteSpace(reportOnlySessionId) ||
                !string.IsNullOrWhiteSpace(dumpSessionId) ||
                !string.IsNullOrWhiteSpace(diagnoseComputerUseArgument) ||
                !string.IsNullOrWhiteSpace(diagnosticsMode);

            if (options.ListSessions && otherOperationsRequested)
            {
                logger.LogError("--list-sessions cannot be combined with other operations.");
                Console.Error.WriteLine("--list-sessions cannot be combined with other operations.");
                return 1;
            }

            if (options.HasDeleteRequest && otherOperationsRequested)
            {
                logger.LogError("Delete operations cannot be combined with other research actions.");
                Console.Error.WriteLine("Delete operations cannot be combined with other research actions.");
                return 1;
            }

            bool diagnosticsRequested = !string.IsNullOrWhiteSpace(diagnosticsMode);
            bool reportOnlyMode = !diagnosticsRequested && !string.IsNullOrWhiteSpace(reportOnlySessionId);

            if (diagnosticsRequested)
            {
                if (!string.IsNullOrWhiteSpace(dumpSessionId) || !string.IsNullOrWhiteSpace(diagnoseComputerUseArgument))
                {
                    logger.LogError("--diagnostics-mode cannot be combined with dump or computer-use diagnosis operations.");
                    Console.Error.WriteLine("--diagnostics-mode cannot be combined with dump or computer-use diagnosis operations.");
                    return 1;
                }

                if (!string.IsNullOrWhiteSpace(providedQuery))
                {
                    logger.LogWarning("Ignoring --query argument because diagnostics mode was requested.");
                    providedQuery = null;
                }

                if (!string.IsNullOrWhiteSpace(reportOnlySessionId))
                {
                    resumeSessionId = reportOnlySessionId;
                }
            }

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

            string? diagnosticsSessionId = null;
            if (diagnosticsRequested)
            {
                diagnosticsSessionId = !string.IsNullOrWhiteSpace(resumeSessionId)
                    ? resumeSessionId
                    : reportOnlySessionId;

                if (string.IsNullOrWhiteSpace(diagnosticsSessionId))
                {
                    logger.LogError("--diagnostics-mode requires --resume or --report-session to specify a session id.");
                    Console.Error.WriteLine("--diagnostics-mode requires --resume or --report-session to specify a session id.");
                    return 1;
                }
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

            var longTermMemory = new LongTermMemoryManager(
                cosmosService,
                openAiService,
                settings.AzureOpenAiEmbeddingDeployment,
                loggerFactory.CreateLogger<LongTermMemoryManager>());

            if (diagnosticsRequested)
            {
                var diagnosticsLogger = loggerFactory.CreateLogger("DiagnosticsMode");
                int diagnosticsExitCode = await RunDiagnosticsModeAsync(
                        diagnosticsMode!,
                        diagnosticsSessionId!,
                        longTermMemory,
                        diagnosticsLogger,
                        cts.Token)
                    .ConfigureAwait(false);
                return diagnosticsExitCode;
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
                catch (ComputerUseOperationTimeoutException timeout)
                {
                    logger.LogError(timeout, "Computer-use diagnosis timed out for {Url}", url);
                    Console.Error.WriteLine($"Computer-use exploration timed out: {timeout.Message}");
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

            var orchestratorOptions = configuration.GetSection("Orchestrator").Get<OrchestratorOptions>() ?? new OrchestratorOptions();
            var researcherOptions = configuration.GetSection("Researcher").Get<ResearcherOptions>() ?? new ResearcherOptions();
            var shortTermOptions = configuration.GetSection("ShortTermMemory").Get<ShortTermMemoryOptions>() ?? new ShortTermMemoryOptions();

            if (computerUseServiceFactory is not null)
            {
                int navigatorParallelism = Math.Max(1, researcherOptions.Parallelism?.NavigatorDegreeOfParallelism ?? 1);
                computerUseExplorerPool = new PooledComputerUseExplorer(
                    navigatorParallelism,
                    () => computerUseServiceFactory(),
                    loggerFactory.CreateLogger<PooledComputerUseExplorer>());
            }

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

                IComputerUseExplorer? navigatorExplorer = computerUseExplorerPool ?? (IComputerUseExplorer?)computerUseSearchService;
                if (navigatorExplorer is not null)
                {
                    tools.Add(new ComputerUseNavigatorTool(
                        navigatorExplorer,
                        loggerFactory.CreateLogger<ComputerUseNavigatorTool>()));
                }

                return tools;
            };

            var planner = new InteractiveResearchPlanner(
                openAiService,
                settings.AzureOpenAiChatDeployment,
                loggerFactory.CreateLogger<InteractiveResearchPlanner>());

            var orchestrator = new OrchestratorAgent(
                openAiService,
                longTermMemory,
                toolFactory,
                settings.AzureOpenAiChatDeployment,
                loggerFactory.CreateLogger<OrchestratorAgent>(),
                loggerFactory,
                cacheService,
                reportsDirectory: reportDirectory,
                orchestratorOptions: orchestratorOptions,
                researcherOptions: researcherOptions,
                shortTermMemoryOptions: shortTermOptions);

            logger.LogInformation("Loading existing research sessions from Cosmos DB.");
            var existingSessionRecords = await cosmosService.QueryByTypeAsync(SessionRecordType, 100, cts.Token).ConfigureAwait(false);
            var sessionLookup = existingSessionRecords.ToDictionary(record => record.ResearchSessionId, StringComparer.OrdinalIgnoreCase);
            var sessionInfos = existingSessionRecords.Select(MapSession).OrderByDescending(info => info.UpdatedAtUtc).ToList();
            logger.LogInformation("{SessionCount} stored sessions loaded.", sessionInfos.Count);

            if (options.ListSessions)
            {
                PrintSessionTable(sessionInfos);
                return 0;
            }

            if (options.HasDeleteRequest)
            {
                int deleteExitCode = await HandleDeleteRequestAsync(options, sessionInfos, sessionLookup, cosmosService, logger, cts.Token).ConfigureAwait(false);
                return deleteExitCode;
            }

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

            var contextHints = new List<string>();

            if (!string.IsNullOrWhiteSpace(providedQuery))
            {
                if (options.SkipPlanner)
                {
                    contextHints.Add("PlannerSkipped: true");
                }
                else
                {
                    try
                    {
                        PlannerOutcome plannerOutcome = await planner.RunNonInteractiveAsync(providedQuery, cts.Token).ConfigureAwait(false);
                        providedQuery = plannerOutcome.Objective;
                        contextHints.AddRange(plannerOutcome.ContextHints);
                        logger.LogInformation("Interactive planner prepared pre-flight plan for objective '{Objective}'.", providedQuery);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Planner pre-check failed for objective '{Objective}'. Proceeding without planner context.", providedQuery);
                    }
                }
            }

            string? researchObjective = providedQuery;
            if (sessionToResume is null && string.IsNullOrWhiteSpace(researchObjective))
            {
                var menuSelection = await RunInteractiveMenuAsync(sessionInfos, sessionLookup, planner, cosmosService, logger, options.SkipPlanner, cts.Token).ConfigureAwait(false);
                if (menuSelection.Action == MenuAction.Exit)
                {
                    logger.LogInformation("No console operation selected. Exiting.");
                    return 0;
                }

                switch (menuSelection.Action)
                {
                    case MenuAction.StartNew:
                        if (menuSelection.PlannerOutcome is null)
                        {
                            logger.LogError("Planner outcome missing after StartNew selection.");
                            return 1;
                        }

                        researchObjective = menuSelection.PlannerOutcome.Objective;
                        contextHints.Clear();
                        contextHints.AddRange(menuSelection.PlannerOutcome.ContextHints);
                        break;
                    case MenuAction.Resume:
                        sessionToResume = menuSelection.SelectedSession;
                        researchObjective = sessionToResume?.Objective;
                        break;
                    case MenuAction.RegenerateReport:
                        sessionToResume = menuSelection.SelectedSession;
                        researchObjective = sessionToResume?.Objective;
                        reportOnlyMode = true;
                        break;
                    default:
                        logger.LogError("Unhandled menu selection {Action}.", menuSelection.Action);
                        return 1;
                }
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
                ContextHints = new List<string>(contextHints)
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
            if (computerUseExplorerPool is not null)
            {
                await computerUseExplorerPool.DisposeAsync().ConfigureAwait(false);
            }

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

    private static async Task<int> RunDiagnosticsModeAsync(
        string diagnosticsMode,
        string sessionId,
        LongTermMemoryManager longTermMemoryManager,
        ILogger diagnosticsLogger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsMode))
        {
            diagnosticsLogger.LogError("Diagnostics mode flag was empty.");
            Console.Error.WriteLine("Diagnostics mode flag cannot be empty.");
            return 1;
        }

        string normalizedMode = diagnosticsMode.Trim().ToLowerInvariant();
        switch (normalizedMode)
        {
            case "list-sources":
            case "sources":
                var findings = await longTermMemoryManager
                    .RestoreFindingsAsync(sessionId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (findings.Count == 0)
                {
                    diagnosticsLogger.LogWarning("No stored findings available for session {SessionId}.", sessionId);
                    Console.WriteLine($"No stored findings available for session {sessionId}.");
                    return 0;
                }

                var uniqueCitations = CollectUniqueCitations(findings);
                if (uniqueCitations.Count == 0)
                {
                    diagnosticsLogger.LogInformation("Session {SessionId} contains no stored citations.", sessionId);
                    Console.WriteLine($"No stored citations available for session {sessionId}.");
                    return 0;
                }

                var reportBuilder = new MarkdownReportBuilder();
                string sourcesBlock = reportBuilder.BuildSourcesSection(uniqueCitations);
                Console.WriteLine($"# Diagnostics: Sources for {sessionId}");
                Console.WriteLine();
                Console.WriteLine(sourcesBlock);
                return 0;

            default:
                diagnosticsLogger.LogError("Unknown diagnostics mode: {Mode}", diagnosticsMode);
                Console.Error.WriteLine($"Unknown diagnostics mode: {diagnosticsMode}");
                return 1;
        }
    }

    private static IReadOnlyList<SourceCitation> CollectUniqueCitations(IEnumerable<ResearchFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var allCitations = findings
            .Where(f => f?.Citations is not null)
            .SelectMany(f => f.Citations!)
            .Where(c => c is not null)
            .ToList();

        return SourceCitationDeduplicator.Deduplicate(allCitations);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --query <text>                Start a new research session with the provided objective.");
        Console.WriteLine("  --resume <session-id>         Resume an existing research session.");
        Console.WriteLine("  --reports <path>              Override the directory used for report output.");
        Console.WriteLine("  --dump-session <session-id>   Export session artifacts to disk.");
        Console.WriteLine("      --dump-type <types>       Optional comma-separated list of memory record types to export.");
        Console.WriteLine("      --dump-dir <path>         Destination directory for session dump output.");
        Console.WriteLine("      --dump-limit <N>          Maximum number of records to export (default 200).");
        Console.WriteLine("  --diagnose-computer-use <url|objective>");
        Console.WriteLine("                                Run a single computer-use exploration for diagnostics.");
        Console.WriteLine("  --report-session <session-id> Regenerate the report for an existing session.");
        Console.WriteLine("  --diagnostics-mode <mode>     Run diagnostics utilities (e.g., list-sources).");
        Console.WriteLine("  --delete-session <session-id> Delete all memory records for the specified session.");
        Console.WriteLine("  --delete-incomplete           Delete all sessions that are not marked completed.");
        Console.WriteLine("  --delete-all                  Delete all stored research sessions.");
        Console.WriteLine("  --skip-planner                Skip the interactive research planner pre-check.");
        Console.WriteLine("  --list-sessions               List stored sessions and exit.");
        Console.WriteLine("  --cosmos-diagnostics          Run Cosmos DB diagnostics and exit.");
        Console.WriteLine("  --help, -h                    Show this message and exit.");
    }

    private static string BuildExplorationSummary(ComputerUseExplorationResult exploration)
    {
        if (!string.IsNullOrWhiteSpace(exploration.StructuredSummaryJson))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(exploration.StructuredSummaryJson);
                JsonElement root = document.RootElement;

                var summaryBuilder = new StringBuilder();

                if (root.TryGetProperty("summary", out JsonElement summaryElement) && summaryElement.ValueKind == JsonValueKind.String)
                {
                    string? summary = summaryElement.GetString();
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        summaryBuilder.AppendLine("Summary:");
                        summaryBuilder.AppendLine(summary.Trim());
                        summaryBuilder.AppendLine();
                    }
                }

                if (root.TryGetProperty("findings", out JsonElement findingsElement) && findingsElement.ValueKind == JsonValueKind.Array)
                {
                    var findings = findingsElement
                        .EnumerateArray()
                        .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item!.Trim())
                        .ToArray();

                    if (findings.Length > 0)
                    {
                        summaryBuilder.AppendLine("Findings:");
                        foreach (string finding in findings)
                        {
                            summaryBuilder.AppendLine($" - {finding}");
                        }
                        summaryBuilder.AppendLine();
                    }
                }

                if (root.TryGetProperty("flagged", out JsonElement flaggedElement) && flaggedElement.ValueKind == JsonValueKind.Array)
                {
                    var flaggedEntries = new List<(string Title, string Url, string? Type)>();

                    foreach (JsonElement item in flaggedElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string? title = item.TryGetProperty("title", out JsonElement titleElement) && titleElement.ValueKind == JsonValueKind.String
                            ? titleElement.GetString()
                            : null;
                        string? url = item.TryGetProperty("url", out JsonElement urlElement) && urlElement.ValueKind == JsonValueKind.String
                            ? urlElement.GetString()
                            : null;
                        string? type = item.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
                            ? typeElement.GetString()
                            : null;

                        if (string.IsNullOrWhiteSpace(url))
                        {
                            continue;
                        }

                        string displayTitle = string.IsNullOrWhiteSpace(title) ? url! : title!.Trim();
                        string? trimmedType = string.IsNullOrWhiteSpace(type) ? null : type!.Trim();
                        flaggedEntries.Add((displayTitle, url!.Trim(), trimmedType));
                    }

                    if (flaggedEntries.Count > 0)
                    {
                        summaryBuilder.AppendLine("Flagged Resources:");
                        foreach ((string Title, string Url, string? Type) entry in flaggedEntries)
                        {
                            string suffix = string.IsNullOrWhiteSpace(entry.Type) ? string.Empty : $" [{entry.Type}]";
                            summaryBuilder.AppendLine($" - {entry.Title}{suffix}\n   {entry.Url}");
                        }
                        summaryBuilder.AppendLine();
                    }
                }

                string formatted = summaryBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    return formatted;
                }
            }
            catch (JsonException)
            {
                // Fall back to raw payload if parsing fails.
            }

            return exploration.StructuredSummaryJson!.Trim();
        }

        if (exploration.Transcript.Count == 0)
        {
            return "No summary was produced by the exploration session.";
        }

        var transcriptBuilder = new StringBuilder();
        foreach (string segment in exploration.Transcript.TakeLast(3))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (transcriptBuilder.Length > 0)
            {
                transcriptBuilder.AppendLine();
            }

            transcriptBuilder.Append(segment.Trim());
        }

        return transcriptBuilder.Length == 0
            ? "No summary was produced by the exploration session."
            : transcriptBuilder.ToString();
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

    private static async Task<MenuSelection> RunInteractiveMenuAsync(
        List<SessionInfo> sessions,
        Dictionary<string, MemoryRecord> sessionLookup,
        InteractiveResearchPlanner planner,
        ICosmosMemoryService cosmosService,
        ILogger logger,
        bool skipPlanner,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DisplaySessionsOverview(sessions);
            Console.WriteLine();
            Console.WriteLine("Select an option:");
            Console.WriteLine("  1) Start new research session");
            Console.WriteLine("  2) Resume existing session");
            Console.WriteLine("  3) Regenerate completed report");
            Console.WriteLine("  4) Delete sessions");
            Console.WriteLine("  5) Exit console");
            Console.Write("Choice: ");

            string? input = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim().ToLowerInvariant();
            switch (input)
            {
                case "1":
                case "start":
                case "n":
                    Console.Write("Enter a research objective: ");
                    string? objective = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
                    if (string.IsNullOrWhiteSpace(objective))
                    {
                        Console.WriteLine("Objective cannot be empty.");
                        continue;
                    }

                    if (skipPlanner)
                    {
                        var hints = new List<string> { "PlannerSkipped: true" };
                        var skippedOutcome = new PlannerOutcome(objective, "Planner skipped by request.", Array.Empty<string>(), hints);
                        return new MenuSelection(MenuAction.StartNew, null, skippedOutcome);
                    }

                    PlannerOutcome? plannerOutcome = await planner.RunAsync(objective, cancellationToken).ConfigureAwait(false);
                    if (plannerOutcome is null)
                    {
                        Console.WriteLine("Planner cancelled. Returning to menu.");
                        continue;
                    }

                    return new MenuSelection(MenuAction.StartNew, null, plannerOutcome);

                case "2":
                case "resume":
                case "r":
                    {
                        SessionInfo? resumeSession = await SelectSessionAsync(
                            sessions,
                            static _ => true,
                            "Select a session to resume",
                            cancellationToken).ConfigureAwait(false);

                        if (resumeSession is null)
                        {
                            continue;
                        }

                        return new MenuSelection(MenuAction.Resume, resumeSession, null);
                    }

                case "3":
                case "report":
                case "g":
                    {
                        SessionInfo? completedSession = await SelectSessionAsync(
                            sessions,
                            static session => IsSessionCompleted(session),
                            "Select a completed session to regenerate",
                            cancellationToken).ConfigureAwait(false);

                        if (completedSession is null)
                        {
                            continue;
                        }

                        return new MenuSelection(MenuAction.RegenerateReport, completedSession, null);
                    }

                case "4":
                case "delete":
                case "d":
                    {
                        bool deleted = await HandleDeletionMenuAsync(
                            sessions,
                            sessionLookup,
                            cosmosService,
                            logger,
                            cancellationToken).ConfigureAwait(false);

                        if (deleted)
                        {
                            Console.WriteLine("Deletion complete. Returning to menu.");
                        }
                        continue;
                    }

                case "5":
                case "exit":
                case "q":
                    return new MenuSelection(MenuAction.Exit, null, null);

                default:
                    Console.WriteLine("Unknown selection. Choose 1-5.");
                    continue;
            }
        }
    }

    private static void DisplaySessionsOverview(IReadOnlyList<SessionInfo> sessions)
    {
        Console.WriteLine();
        Console.WriteLine("# Research Console");
        if (sessions.Count == 0)
        {
            Console.WriteLine("No stored sessions. Start a new investigation!");
            return;
        }

        Console.WriteLine($"Stored sessions: {sessions.Count}");
        var grouped = sessions
            .GroupBy(session => string.IsNullOrWhiteSpace(session.Status) ? "unknown" : session.Status.ToLowerInvariant())
            .OrderByDescending(group => group.Count());
        foreach (var group in grouped)
        {
            Console.WriteLine($" - {group.Key}: {group.Count()}");
        }

        Console.WriteLine();
        Console.WriteLine("Recent sessions:");
        foreach (var session in sessions.Take(8))
        {
            Console.WriteLine($"  {session.SessionId} | {session.Status} | {session.UpdatedAtUtc:yyyy-MM-dd HH:mm}Z | {Truncate(session.Objective, 72)}");
        }
    }

    private static async Task<bool> HandleDeletionMenuAsync(
        List<SessionInfo> sessions,
        Dictionary<string, MemoryRecord> sessionLookup,
        ICosmosMemoryService cosmosService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Delete options:");
        Console.WriteLine("  1) Delete a specific session");
        Console.WriteLine("  2) Delete all incomplete sessions");
        Console.WriteLine("  3) Delete all sessions");
        Console.WriteLine("  4) Cancel");
        Console.Write("Choice: ");

        string? input = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim().ToLowerInvariant();
        switch (input)
        {
            case "1":
            case "session":
                {
                    IReadOnlyList<SessionInfo> targets = await SelectSessionsForDeletionAsync(
                        sessions,
                        cancellationToken).ConfigureAwait(false);

                    if (targets.Count == 0)
                    {
                        return false;
                    }

                    if (targets.Count == 1)
                    {
                        string sessionId = targets[0].SessionId;
                        bool confirmedSingle = await ConfirmDeletionAsync(
                                $"Type DELETE to confirm removal of session {sessionId}: ",
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!confirmedSingle)
                        {
                            Console.WriteLine("Deletion cancelled.");
                            return false;
                        }
                    }
                    else
                    {
                        Console.WriteLine("Selected sessions:");
                        foreach (var session in targets)
                        {
                            Console.WriteLine($" - {session.SessionId} | {session.Status} | {Truncate(session.Objective, 72)}");
                        }

                        bool confirmedMultiple = await ConfirmDeletionAsync(
                                $"Type DELETE to confirm removal of {targets.Count} session(s): ",
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!confirmedMultiple)
                        {
                            Console.WriteLine("Deletion cancelled.");
                            return false;
                        }
                    }

                    int deletedRecords = await DeleteSessionsAsync(targets, cosmosService, logger, cancellationToken).ConfigureAwait(false);
                    RemoveSessionsFromCollections(targets, sessions, sessionLookup);

                    if (targets.Count == 1)
                    {
                        Console.WriteLine($"Deleted session {targets[0].SessionId} (removed {deletedRecords} record(s)).");
                    }
                    else
                    {
                        Console.WriteLine($"Deleted {targets.Count} session(s) (removed {deletedRecords} record(s)).");
                    }

                    return true;
                }

            case "2":
            case "incomplete":
                {
                    var incompleteSessions = sessions.Where(session => !IsSessionCompleted(session)).ToList();
                    if (incompleteSessions.Count == 0)
                    {
                        Console.WriteLine("No incomplete sessions to delete.");
                        return false;
                    }

                    bool confirmed = await ConfirmDeletionAsync(
                        $"Type DELETE to remove {incompleteSessions.Count} incomplete session(s): ",
                        cancellationToken).ConfigureAwait(false);
                    if (!confirmed)
                    {
                        Console.WriteLine("Deletion cancelled.");
                        return false;
                    }

                    int deletedRecords = await DeleteSessionsAsync(incompleteSessions, cosmosService, logger, cancellationToken).ConfigureAwait(false);
                    RemoveSessionsFromCollections(incompleteSessions, sessions, sessionLookup);
                    Console.WriteLine($"Deleted {incompleteSessions.Count} incomplete session(s) (removed {deletedRecords} record(s)).");
                    return true;
                }

            case "3":
            case "all":
                {
                    if (sessions.Count == 0)
                    {
                        Console.WriteLine("No sessions stored.");
                        return false;
                    }

                    bool confirmed = await ConfirmDeletionAsync(
                        $"Type DELETE to remove ALL {sessions.Count} session(s): ",
                        cancellationToken).ConfigureAwait(false);
                    if (!confirmed)
                    {
                        Console.WriteLine("Deletion cancelled.");
                        return false;
                    }

                    int deletedRecords = await DeleteSessionsAsync(sessions.ToList(), cosmosService, logger, cancellationToken).ConfigureAwait(false);
                    sessionLookup.Clear();
                    sessions.Clear();
                    Console.WriteLine($"Deleted all sessions (removed {deletedRecords} record(s)).");
                    return true;
                }

            default:
                Console.WriteLine("Deletion cancelled.");
                return false;
        }
    }

    private static async Task<SessionInfo?> SelectSessionAsync(
        IReadOnlyList<SessionInfo> sessions,
        Func<SessionInfo, bool> filter,
        string prompt,
        CancellationToken cancellationToken)
    {
        var filtered = sessions.Where(filter).ToList();
        if (filtered.Count == 0)
        {
            Console.WriteLine("No sessions available for this action.");
            return null;
        }

        Console.WriteLine();
        Console.WriteLine(prompt);
        for (int index = 0; index < filtered.Count; index++)
        {
            var session = filtered[index];
            Console.WriteLine($"[{index + 1}] {session.SessionId} | {session.Status} | {session.UpdatedAtUtc:yyyy-MM-dd HH:mm}Z | {Truncate(session.Objective, 72)}");
        }

        Console.Write("Enter a number, session ID, or 'b' to cancel: ");
        string? selection = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrWhiteSpace(selection) || selection.Equals("b", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (int.TryParse(selection, out int indexChoice) && indexChoice >= 1 && indexChoice <= filtered.Count)
        {
            return filtered[indexChoice - 1];
        }

        SessionInfo? match = filtered.FirstOrDefault(session => string.Equals(session.SessionId, selection, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        Console.WriteLine("Invalid selection.");
        return null;
    }

    private static async Task<IReadOnlyList<SessionInfo>> SelectSessionsForDeletionAsync(
        IReadOnlyList<SessionInfo> sessions,
        CancellationToken cancellationToken)
    {
        if (sessions.Count == 0)
        {
            Console.WriteLine("No sessions available for this action.");
            return Array.Empty<SessionInfo>();
        }

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("Select session(s) to delete");
            for (int index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                Console.WriteLine($"[{index + 1}] {session.SessionId} | {session.Status} | {session.UpdatedAtUtc:yyyy-MM-dd HH:mm}Z | {Truncate(session.Objective, 72)}");
            }

            Console.Write("Enter number(s) or ranges (e.g., 1,3-5). Session IDs are also accepted. Enter 'b' to cancel: ");
            string? input = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();

            if (string.IsNullOrWhiteSpace(input) || input.Equals("b", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<SessionInfo>();
            }

            if (TryResolveSessionSelections(input, sessions, out var selected))
            {
                return selected;
            }

            Console.WriteLine("Invalid selection. Use comma-separated numbers, ranges (e.g., 1-3), or session IDs.");
        }
    }

    private static bool TryResolveSessionSelections(
        string input,
        IReadOnlyList<SessionInfo> sessions,
        out List<SessionInfo> selected)
    {
        ArgumentNullException.ThrowIfNull(sessions);

    var selectedSessions = new List<SessionInfo>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] tokens = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            selected = new List<SessionInfo>();
            return false;
        }

        foreach (string token in tokens)
        {
            if (token.Contains('-', StringComparison.Ordinal))
            {
                string[] parts = token.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int end))
                {
                    if (!AddRange(start, end))
                    {
                        selected = new List<SessionInfo>();
                        return false;
                    }

                    continue;
                }
            }

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int indexValue))
            {
                if (!AddIndex(indexValue))
                {
                    selected = new List<SessionInfo>();
                    return false;
                }

                continue;
            }

            SessionInfo? match = sessions.FirstOrDefault(session => string.Equals(session.SessionId, token, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                selected = new List<SessionInfo>();
                return false;
            }

            if (seen.Add(match.SessionId))
            {
                selectedSessions.Add(match);
            }
        }

        if (selectedSessions.Count == 0)
        {
            selected = new List<SessionInfo>();
            return false;
        }

        selected = selectedSessions;
        return true;

        bool AddRange(int start, int end)
        {
            if (start <= 0 || end <= 0)
            {
                return false;
            }

            if (end < start)
            {
                (start, end) = (end, start);
            }

            for (int index = start; index <= end; index++)
            {
                if (!AddIndex(index))
                {
                    return false;
                }
            }

            return true;
        }

        bool AddIndex(int index)
        {
            if (index < 1 || index > sessions.Count)
            {
                return false;
            }

            var session = sessions[index - 1];
            if (seen.Add(session.SessionId))
            {
                selectedSessions.Add(session);
            }

            return true;
        }
    }

    private static async Task<bool> ConfirmDeletionAsync(string prompt, CancellationToken cancellationToken)
    {
        Console.Write(prompt);
        string? confirmation = (await ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        return string.Equals(confirmation, "DELETE", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> HandleDeleteRequestAsync(
        CommandLineOptions options,
        List<SessionInfo> sessions,
        Dictionary<string, MemoryRecord> sessionLookup,
        ICosmosMemoryService cosmosService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var targets = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);

        if (options.DeleteAll)
        {
            foreach (var session in sessions)
            {
                targets[session.SessionId] = session;
            }
        }

        if (options.DeleteIncomplete)
        {
            foreach (var session in sessions.Where(static session => !IsSessionCompleted(session)))
            {
                targets[session.SessionId] = session;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.DeleteSessionId))
        {
            var match = sessions.FirstOrDefault(session => string.Equals(session.SessionId, options.DeleteSessionId, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                logger.LogError("Session {SessionId} not found for deletion.", options.DeleteSessionId);
                Console.Error.WriteLine($"No session found with ID '{options.DeleteSessionId}'.");
                return 1;
            }

            targets[match.SessionId] = match;
        }

        if (targets.Count == 0)
        {
            Console.WriteLine("No sessions matched the deletion request.");
            return 0;
        }

        var toDelete = targets.Values.ToList();
        Console.WriteLine("# Deleting sessions");
        foreach (var session in toDelete)
        {
            Console.WriteLine($" - {session.SessionId} | {session.Status} | {Truncate(session.Objective, 80)}");
        }

        int deletedRecords = await DeleteSessionsAsync(toDelete, cosmosService, logger, cancellationToken).ConfigureAwait(false);
        RemoveSessionsFromCollections(toDelete, sessions, sessionLookup);

        logger.LogInformation("CLI deletion removed {SessionCount} session(s) and {RecordCount} record(s).", toDelete.Count, deletedRecords);
        Console.WriteLine($"Deleted {toDelete.Count} session(s); removed {deletedRecords} record(s).");
        return 0;
    }

    private static async Task<int> DeleteSessionsAsync(
        IEnumerable<SessionInfo> sessions,
        ICosmosMemoryService cosmosService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sessionList = sessions.ToList();
        int totalRecords = 0;
        foreach (var session in sessionList)
        {
            int removed = await cosmosService.DeleteSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            totalRecords += removed;
            DeleteReportArtifacts(session, logger);
        }

        if (sessionList.Count > 0)
        {
            logger.LogInformation(
                "Deleted {SessionCount} session(s); removed {RecordCount} record(s) from Cosmos.",
                sessionList.Count,
                totalRecords);
        }

        return totalRecords;
    }

    private static void RemoveSessionsFromCollections(
        IEnumerable<SessionInfo> deleted,
        List<SessionInfo> sessions,
        Dictionary<string, MemoryRecord> sessionLookup)
    {
        foreach (var session in deleted)
        {
            sessions.RemoveAll(info => string.Equals(info.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase));
            sessionLookup.Remove(session.SessionId);
        }
    }

    private static bool IsSessionCompleted(SessionInfo session)
        => string.Equals(session.Status, "completed", StringComparison.OrdinalIgnoreCase);

    private static void DeleteReportArtifacts(SessionInfo session, ILogger logger)
    {
        if (session.Record.Metadata.TryGetValue("lastReportPath", out var lastReport) && !string.IsNullOrWhiteSpace(lastReport))
        {
            TryDeleteFile(lastReport, logger);
        }

        if (session.Record.Metadata.TryGetValue("reportPath", out var reportPath) && !string.IsNullOrWhiteSpace(reportPath))
        {
            TryDeleteFile(reportPath, logger);
        }
    }

    private static void TryDeleteFile(string path, ILogger logger)
    {
        try
        {
            string resolved = Path.GetFullPath(path);
            if (File.Exists(resolved))
            {
                File.Delete(resolved);
                logger.LogInformation("Deleted artifact {Path}.", resolved);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete artifact {Path}.", path);
        }
    }

    private static void PrintSessionTable(IReadOnlyList<SessionInfo> sessions)
    {
        Console.WriteLine("# Stored Sessions");
        if (sessions.Count == 0)
        {
            Console.WriteLine("No sessions found.");
            return;
        }

        Console.WriteLine("SessionId            | Status      | Updated (UTC)       | Objective");
        Console.WriteLine(new string('-', 90));
        foreach (var session in sessions.Take(25))
        {
            Console.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-20} | {1,-10} | {2:yyyy-MM-dd HH:mm} | {3}",
                    session.SessionId,
                    session.Status,
                    session.UpdatedAtUtc,
                    Truncate(session.Objective, 60)));
        }

        if (sessions.Count > 25)
        {
            Console.WriteLine($"... ({sessions.Count - 25} more not shown)");
        }
    }

    private static string Truncate(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value!.Length <= length ? value : value[..length] + "…";
    }

    private static async Task<string?> ReadLineAsync(CancellationToken token)
    {
        return await Task.Run(Console.ReadLine, token).ConfigureAwait(false);
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





















