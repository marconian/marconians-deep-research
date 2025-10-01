using System.Collections.Generic;
using System.IO;
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

    public static AppSettings LoadAndValidate()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var missing = new List<string>();

        string ReadRequired(string key)
        {
            string? value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
                return string.Empty;
            }

            return value.Trim();
        }

        var settings = new AppSettings
        {
            AzureOpenAiEndpoint = ReadRequired("AZURE_OPENAI_ENDPOINT"),
            AzureOpenAiApiKey = ReadRequired("AZURE_OPENAI_API_KEY"),
            AzureOpenAiChatDeployment = ReadRequired("AZURE_OPENAI_CHAT_DEPLOYMENT"),
            AzureOpenAiEmbeddingDeployment = ReadRequired("AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
            AzureOpenAiVisionDeployment = ReadRequired("AZURE_OPENAI_VISION_DEPLOYMENT"),
            CosmosConnectionString = ReadRequired("COSMOS_CONN_STRING"),
            CognitiveServicesEndpoint = ReadRequired("COGNITIVE_SERVICES_ENDPOINT"),
            CognitiveServicesApiKey = ReadRequired("COGNITIVE_SERVICES_API_KEY"),
            GoogleApiKey = ReadRequired("GOOGLE_API_KEY"),
            GoogleSearchEngineId = ReadRequired("GOOGLE_SEARCH_ENGINE_ID"),
            CacheDirectory = ResolveCacheDirectory(configuration["CACHE_DIRECTORY"]),
            PrimaryResearchObjective = configuration["PRIMARY_RESEARCH_OBJECTIVE"]?.Trim()
        };

        if (missing.Count > 0)
        {
            string message = $"Missing required environment variables: {string.Join(", ", missing)}";
            throw new InvalidOperationException(message);
        }

        return settings;
    }

    public static IReadOnlyList<string> RequiredVariables => RequiredEnvironmentVariables;

    private static string ResolveCacheDirectory(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "debug", "cache");
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
        public required string CacheDirectory { get; init; }
        public string? PrimaryResearchObjective { get; init; }
    }
}
