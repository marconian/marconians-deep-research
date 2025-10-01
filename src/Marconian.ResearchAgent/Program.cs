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
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.Cosmos;
using Marconian.ResearchAgent.Services.Files;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Tools;
using System.Linq;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using System.Collections.ObjectModel;
using Marconian.ResearchAgent.Logging;

namespace Marconian.ResearchAgent;

internal static class Program
{
    private const string SessionRecordType = "session_metadata";

    private sealed record SessionInfo(string SessionId, string Objective, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, MemoryRecord Record);

    private static async Task<int> Main(string[] args)
    {
        bool cosmosDiagnostics = args.Any(argument => string.Equals(argument, "--cosmos-diagnostics", StringComparison.OrdinalIgnoreCase));
        string defaultReportDirectory = Path.Combine(Directory.GetCurrentDirectory(), "debug", "reports");
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

        try
        {
            logger.LogInformation("Starting Marconian research agent.");

            Settings.AppSettings settings;
            try
            {
                settings = Settings.LoadAndValidate();
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

            logger.LogInformation("Initializing Redis cache client.");
            await using var redisCache = new RedisCacheService(settings, loggerFactory.CreateLogger<RedisCacheService>());

            var openAiService = new AzureOpenAiService(settings, loggerFactory.CreateLogger<AzureOpenAiService>());
            var longTermMemory = new LongTermMemoryManager(
                cosmosService,
                openAiService,
                settings.AzureOpenAiEmbeddingDeployment,
                loggerFactory.CreateLogger<LongTermMemoryManager>());
            var documentService = new DocumentIntelligenceService(settings, loggerFactory.CreateLogger<DocumentIntelligenceService>());
            var fileRegistryService = new FileRegistryService();
            using var sharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            Func<IEnumerable<ITool>> toolFactory = () => new ITool[]
            {
                new WebSearchTool(settings.GoogleApiKey, settings.GoogleSearchEngineId, redisCache, sharedHttpClient, loggerFactory.CreateLogger<WebSearchTool>()),
                new WebScraperTool(redisCache, sharedHttpClient, loggerFactory.CreateLogger<WebScraperTool>()),
                new FileReaderTool(fileRegistryService, documentService, sharedHttpClient, loggerFactory.CreateLogger<FileReaderTool>()),
                new ImageReaderTool(fileRegistryService, openAiService, settings.AzureOpenAiVisionDeployment, sharedHttpClient, loggerFactory.CreateLogger<ImageReaderTool>())
            };

            ParseCommandLine(args, out string? resumeSessionId, out string? providedQuery, out string? reportDirectoryArgument);

            string reportDirectory = string.IsNullOrWhiteSpace(reportDirectoryArgument)
                ? defaultReportDirectory
                : Path.GetFullPath(reportDirectoryArgument);

            var orchestrator = new OrchestratorAgent(
                openAiService,
                longTermMemory,
                toolFactory,
                settings.AzureOpenAiChatDeployment,
                loggerFactory.CreateLogger<OrchestratorAgent>(),
                loggerFactory,
                redisCache,
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

            Console.WriteLine(sessionToResume is null
                ? $"Starting new research session '{sessionId}' for objective: {researchObjective}"
                : $"Resuming session '{sessionId}' (last status: {sessionToResume.Status}) for objective: {researchObjective}");

            logger.LogInformation("Executing research objective '{Objective}' for session {SessionId}.", researchObjective, sessionId);

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
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.UnhandledException -= unhandledExceptionHandler;
        }
    }

    private static void ParseCommandLine(string[] args, out string? resumeSessionId, out string? query, out string? reportsDirectory)
    {
        resumeSessionId = null;
        query = null;
        reportsDirectory = null;

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
                case "--help":
                case "-h":
                    Console.WriteLine("Usage: dotnet run [--query \"question\"] [--resume sessionId] [--reports path]");
                    Environment.Exit(0);
                    break;
            }
        }
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
