using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Marconian.ResearchAgent.Services.ComputerUse;
using Marconian.ResearchAgent.Streaming;
using Microsoft.Extensions.Configuration;

namespace Marconian.ResearchAgent.Configuration;

public static class Settings
{
    private static readonly string[] RequiredEnvironmentVariables =
    [
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_CHAT_DEPLOYMENT",
        "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
        "AZURE_OPENAI_VISION_DEPLOYMENT",
        "COSMOS_CONN_STRING",
        "COGNITIVE_SERVICES_ENDPOINT",
        "COGNITIVE_SERVICES_API_KEY",
        "GOOGLE_API_KEY",
        "GOOGLE_SEARCH_ENGINE_ID",
        "PRIMARY_RESEARCH_OBJECTIVE"
    ];

    public static IConfigurationRoot BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    public static AppSettings LoadAndValidate()
        => LoadAndValidate(BuildConfiguration());

    public static AppSettings LoadAndValidate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var missing = new List<string>();

        string ReadRequired(string envKey, params string[] structuredKeys)
        {
            foreach (string key in structuredKeys)
            {
                string? value = configuration[key];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            string? fallback = configuration[envKey];
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback.Trim();
            }

            missing.Add(envKey);
            return string.Empty;
        }

        string? ReadOptional(params string[] keys)
        {
            foreach (string key in keys)
            {
                string? value = configuration[key];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        string? providerRaw = ReadOptional("Search:Provider", "WebSearch:Provider", "WEB_SEARCH_PROVIDER");
        WebSearchProvider provider = WebSearchProvider.GoogleApi;
        if (!string.IsNullOrWhiteSpace(providerRaw) &&
            !Enum.TryParse(providerRaw.Trim(), ignoreCase: true, out provider))
        {
            missing.Add("WEB_SEARCH_PROVIDER");
            provider = WebSearchProvider.GoogleApi;
        }

        string googleApiKey = ReadOptional("Google:ApiKey", "GOOGLE_API_KEY") ?? string.Empty;
        string googleSearchEngineId = ReadOptional("Google:SearchEngineId", "GOOGLE_SEARCH_ENGINE_ID") ?? string.Empty;

        string? computerUseDeployment = ReadOptional("AzureOpenAI:ComputerUseDeployment", "AZURE_OPENAI_COMPUTER_USE_DEPLOYMENT");

        bool computerUseEnabled = true;
        string? computerUseEnabledRaw = ReadOptional("ComputerUse:Enabled", "COMPUTER_USE_ENABLED");
        if (!string.IsNullOrWhiteSpace(computerUseEnabledRaw) &&
            !bool.TryParse(computerUseEnabledRaw, out computerUseEnabled))
        {
            throw new InvalidOperationException("COMPUTER_USE_ENABLED must be either 'true' or 'false'.");
        }

        ComputerUseMode computerUseMode = ComputerUseMode.Hybrid;
        string? computerUseModeRaw = ReadOptional("ComputerUse:Mode", "Search:ComputerUseMode", "COMPUTER_USE_MODE");
        if (!string.IsNullOrWhiteSpace(computerUseModeRaw) &&
            !Enum.TryParse(computerUseModeRaw, ignoreCase: true, out computerUseMode))
        {
            throw new InvalidOperationException("COMPUTER_USE_MODE must be either 'Hybrid' or 'Full'.");
        }

        var stageConfigurations = new Dictionary<string, AzureOpenAiStageConfiguration>(StringComparer.OrdinalIgnoreCase);
        var reasoningByDeployment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        static void PopulateStageConfigurations(
            IConfiguration configurationSource,
            string sectionPath,
            IDictionary<string, AzureOpenAiStageConfiguration> target,
            IDictionary<string, string?> reasoningTarget)
        {
            IConfigurationSection section = configurationSource.GetSection(sectionPath);
            if (!section.Exists())
            {
                return;
            }

            foreach (IConfigurationSection stageSection in section.GetChildren())
            {
                string stageName = stageSection.Key;
                string? deployment = stageSection["Deployment"];
                if (string.IsNullOrWhiteSpace(deployment))
                {
                    deployment = stageSection["Model"];
                }

                deployment = string.IsNullOrWhiteSpace(deployment) ? null : deployment.Trim();

                string? reasoningRaw = stageSection["ReasoningEffortLevel"];
                bool reasoningSpecified = stageSection.GetChildren()
                    .Any(child => string.Equals(child.Key, "ReasoningEffortLevel", StringComparison.OrdinalIgnoreCase));

                string? reasoningValue = string.IsNullOrWhiteSpace(reasoningRaw)
                    ? null
                    : reasoningRaw.Trim();

                var config = new AzureOpenAiStageConfiguration
                {
                    Deployment = deployment,
                    ReasoningEffortLevel = reasoningValue,
                    HasExplicitReasoning = reasoningSpecified
                };

                target[stageName] = config;

                if (!string.IsNullOrWhiteSpace(deployment) && reasoningSpecified)
                {
                    reasoningTarget[deployment] = string.IsNullOrWhiteSpace(reasoningValue)
                        ? null
                        : reasoningValue;
                }
            }
        }

        PopulateStageConfigurations(configuration, "AzureOpenAI:Stages", stageConfigurations, reasoningByDeployment);
        PopulateStageConfigurations(configuration, "AzureOpenAI:Deployments", stageConfigurations, reasoningByDeployment);

        AzureOpenAiStageConfiguration? researchStage = null;
        if (stageConfigurations.TryGetValue("Research", out var configuredResearchStage))
        {
            researchStage = configuredResearchStage;
        }
        else if (stageConfigurations.TryGetValue("Default", out var defaultStage))
        {
            researchStage = defaultStage;
        }

        string azureOpenAiEndpoint = ReadRequired("AZURE_OPENAI_ENDPOINT", "AzureOpenAI:Endpoint");
        string azureOpenAiApiKey = ReadRequired("AZURE_OPENAI_API_KEY", "AzureOpenAI:ApiKey");
        string azureOpenAiEmbeddingDeployment = ReadRequired("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "AzureOpenAI:EmbeddingDeployment");
        string azureOpenAiVisionDeployment = ReadRequired("AZURE_OPENAI_VISION_DEPLOYMENT", "AzureOpenAI:VisionDeployment");
        string cosmosConnectionString = ReadRequired("COSMOS_CONN_STRING", "Cosmos:ConnectionString");
        string cognitiveServicesEndpoint = ReadRequired("COGNITIVE_SERVICES_ENDPOINT", "CognitiveServices:Endpoint");
        string cognitiveServicesApiKey = ReadRequired("COGNITIVE_SERVICES_API_KEY", "CognitiveServices:ApiKey");

        string azureOpenAiChatDeployment = !string.IsNullOrWhiteSpace(researchStage?.Deployment)
            ? researchStage!.Deployment!
            : ReadRequired("AZURE_OPENAI_CHAT_DEPLOYMENT", "AzureOpenAI:ChatDeployment", "AzureOpenAI:DefaultChatDeployment");

        string? reasoningEffortLevel = researchStage?.HasExplicitReasoning == true
            ? researchStage.ReasoningEffortLevel
            : ReadOptional("AzureOpenAI:ReasoningEffortLevel", "AZURE_OPENAI_REASONING_EFFORT_LEVEL");

        if (provider == WebSearchProvider.GoogleApi)
        {
            if (string.IsNullOrWhiteSpace(googleApiKey))
            {
                missing.Add("GOOGLE_API_KEY");
            }

            if (string.IsNullOrWhiteSpace(googleSearchEngineId))
            {
                missing.Add("GOOGLE_SEARCH_ENGINE_ID");
            }
        }
        else if (provider == WebSearchProvider.ComputerUse)
        {
            if (string.IsNullOrWhiteSpace(computerUseDeployment))
            {
                missing.Add("AZURE_OPENAI_COMPUTER_USE_DEPLOYMENT");
            }
        }

        ComputerUseOptions computerUseOptions = configuration.GetSection("ComputerUse").Get<ComputerUseOptions>() ?? new();
        ComputerUseTimeoutOptions computerUseTimeouts =
            configuration.GetSection("ComputerUse:Timeouts").Get<ComputerUseTimeoutOptions>()
            ?? ComputerUseTimeoutOptions.Default;
        ConsoleStreamingOptions consoleStreamingOptions = configuration.GetSection("ConsoleStreaming").Get<ConsoleStreamingOptions>() ?? new();

    string cacheDirectory = ResolveDirectory(ReadOptional("Storage:CacheDirectory", "CACHE_DIRECTORY"), Path.Combine("debug", "cache"));
    string reportsDirectory = ResolveDirectory(ReadOptional("Storage:ReportsDirectory", "REPORTS_DIRECTORY"), Path.Combine("debug", "reports"));
    string logsDirectory = ResolveDirectory(ReadOptional("Storage:LogsDirectory", "LOGS_DIRECTORY"), Path.Combine("debug", "logs"));

        string profileDirectory = ResolveDirectory(
            computerUseOptions.UserDataDirectory,
            Path.Combine("debug", "cache", "computer-use-profile"));

        computerUseOptions = computerUseOptions with
        {
            UserDataDirectory = profileDirectory
        };

        var settings = new AppSettings
        {
            AzureOpenAiEndpoint = azureOpenAiEndpoint,
            AzureOpenAiApiKey = azureOpenAiApiKey,
            AzureOpenAiChatDeployment = azureOpenAiChatDeployment,
            AzureOpenAiEmbeddingDeployment = azureOpenAiEmbeddingDeployment,
            AzureOpenAiVisionDeployment = azureOpenAiVisionDeployment,
            CosmosConnectionString = cosmosConnectionString,
            CognitiveServicesEndpoint = cognitiveServicesEndpoint,
            CognitiveServicesApiKey = cognitiveServicesApiKey,
            GoogleApiKey = googleApiKey,
            GoogleSearchEngineId = googleSearchEngineId,
            AzureOpenAiComputerUseDeployment = computerUseDeployment,
            WebSearchProvider = provider,
            ComputerUseEnabled = computerUseEnabled,
            ComputerUseMode = computerUseMode,
            CacheDirectory = cacheDirectory,
            ReportsDirectory = reportsDirectory,
            LogsDirectory = logsDirectory,
            PrimaryResearchObjective = ReadOptional("Research:PrimaryObjective", "PRIMARY_RESEARCH_OBJECTIVE"),
            AzureOpenAiReasoningEffortLevel = reasoningEffortLevel,
            AzureOpenAiStages = stageConfigurations,
            AzureOpenAiDeploymentReasoningLevels = reasoningByDeployment,
            ComputerUse = computerUseOptions,
            ComputerUseTimeouts = computerUseTimeouts,
            ConsoleStreaming = consoleStreamingOptions
        };

        if (missing.Count > 0)
        {
            string message = $"Missing required environment variables: {string.Join(", ", missing)}";
            throw new InvalidOperationException(message);
        }

        return settings;
    }

    public static IReadOnlyList<string> RequiredVariables => RequiredEnvironmentVariables;

    private static string ResolveDirectory(string? configuredPath, string defaultRelativePath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), defaultRelativePath));
        }

        string trimmed = configuredPath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), trimmed));
    }

    public sealed record AppSettings
    {
        public required string AzureOpenAiEndpoint { get; init; }
        public required string AzureOpenAiApiKey { get; init; }
        public required string AzureOpenAiChatDeployment { get; init; }
        public required string AzureOpenAiEmbeddingDeployment { get; init; }
        public required string AzureOpenAiVisionDeployment { get; init; }
        public required string CosmosConnectionString { get; init; }
        public required string CognitiveServicesEndpoint { get; init; }
        public required string CognitiveServicesApiKey { get; init; }
        public required string GoogleApiKey { get; init; }
        public required string GoogleSearchEngineId { get; init; }
        public string? AzureOpenAiComputerUseDeployment { get; init; }
        public WebSearchProvider WebSearchProvider { get; init; } = WebSearchProvider.GoogleApi;
        public bool ComputerUseEnabled { get; init; } = true;
        public ComputerUseMode ComputerUseMode { get; init; } = ComputerUseMode.Hybrid;
        public required string CacheDirectory { get; init; }
        public required string ReportsDirectory { get; init; }
    public required string LogsDirectory { get; init; }
        public string? PrimaryResearchObjective { get; init; }
        public string? AzureOpenAiReasoningEffortLevel { get; init; }
        public IReadOnlyDictionary<string, AzureOpenAiStageConfiguration> AzureOpenAiStages { get; init; }
            = new Dictionary<string, AzureOpenAiStageConfiguration>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string?> AzureOpenAiDeploymentReasoningLevels { get; init; }
            = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        public ComputerUseOptions ComputerUse { get; init; } = new();
        public ComputerUseTimeoutOptions ComputerUseTimeouts { get; init; } = ComputerUseTimeoutOptions.Default;
        public ConsoleStreamingOptions ConsoleStreaming { get; init; } = new();

        public AzureOpenAiStageConfiguration? GetStageConfiguration(string stageName)
            => AzureOpenAiStages.TryGetValue(stageName, out var config) ? config : null;

        public string ResolveStageDeployment(string stageName, string? fallback = null)
        {
            if (AzureOpenAiStages.TryGetValue(stageName, out var config) &&
                !string.IsNullOrWhiteSpace(config.Deployment))
            {
                return config.Deployment!;
            }

            return fallback ?? AzureOpenAiChatDeployment;
        }
    }

    public sealed record AzureOpenAiStageConfiguration
    {
        public string? Deployment { get; init; }
        public string? ReasoningEffortLevel { get; init; }
        public bool HasExplicitReasoning { get; init; }
    }
}

