using Microsoft.Extensions.Configuration;

namespace Marconian.ResearchAgent.Configuration;

/// <summary>
/// Centralized configuration loader that reads required secrets from environment variables.
/// </summary>
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
        "AZURE_REDIS_CACHE_CONN_STRING",
        "COGNITIVE_SERVICES_ENDPOINT",
        "PRIMARY_RESEARCH_OBJECTIVE",
        "COGNITIVE_SERVICES_API_KEY",
        "GOOGLE_API_KEY",
        "GOOGLE_SEARCH_ENGINE_ID"
    ];

    public static AppSettings LoadAndValidate()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var missingVariables = new List<string>();

        string ReadRequired(string key)
        {
            string? value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                missingVariables.Add(key);
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
            RedisConnectionString = ReadRequired("AZURE_REDIS_CACHE_CONN_STRING"),
            CognitiveServicesEndpoint = ReadRequired("COGNITIVE_SERVICES_ENDPOINT"),
            CognitiveServicesApiKey = ReadRequired("COGNITIVE_SERVICES_API_KEY"),
            GoogleApiKey = ReadRequired("GOOGLE_API_KEY"),
            GoogleSearchEngineId = ReadRequired("GOOGLE_SEARCH_ENGINE_ID"),
            PrimaryResearchObjective = configuration["PRIMARY_RESEARCH_OBJECTIVE"]?.Trim();
        };

        if (missingVariables.Count > 0)
        {
            string message = $"Missing required environment variables: {string.Join(", ", missingVariables)}";
            throw new InvalidOperationException(message);
        }

        return settings;
    }

    public static IReadOnlyList<string> RequiredVariables => RequiredEnvironmentVariables;

    public sealed record AppSettings
        public string? PrimaryResearchObjective { get; init; }
        public string? PrimaryResearchObjective { get; init; }
        public string? PrimaryResearchObjective { get; init; }
    {
        public required string AzureOpenAiEndpoint { get; init; }
        public required string AzureOpenAiApiKey { get; init; }
        public required string AzureOpenAiChatDeployment { get; init; }
        public required string AzureOpenAiEmbeddingDeployment { get; init; }
        public required string AzureOpenAiVisionDeployment { get; init; }
        public required string CosmosConnectionString { get; init; }
        public required string RedisConnectionString { get; init; }
        public required string CognitiveServicesEndpoint { get; init; }
        public required string CognitiveServicesApiKey { get; init; }
        public required string GoogleApiKey { get; init; }
        public required string GoogleSearchEngineId { get; init; }
    }
}
