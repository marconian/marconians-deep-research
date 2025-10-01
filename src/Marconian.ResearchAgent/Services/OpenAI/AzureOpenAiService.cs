using System.Linq;
using Azure;
using Azure.AI.OpenAI;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Services.OpenAI;

public sealed class AzureOpenAiService : IAzureOpenAiService
{
    private readonly OpenAIClient _client;
    private readonly ILogger<AzureOpenAiService> _logger;

    public AzureOpenAiService(Settings.AppSettings settings, ILogger<AzureOpenAiService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? NullLogger<AzureOpenAiService>.Instance;
        _client = new OpenAIClient(new Uri(settings.AzureOpenAiEndpoint), new AzureKeyCredential(settings.AzureOpenAiApiKey));
    }

    public async Task<string> GenerateTextAsync(OpenAiChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new ChatCompletionsOptions();
        if (request.Temperature is float temperature)
        {
            options.Temperature = temperature;
        }

        if (request.TopP is float topP)
        {
            options.NucleusSamplingFactor = topP;
        }

        options.Messages.Add(new ChatMessage(ChatRole.System, request.SystemPrompt));
        foreach (var message in request.Messages)
        {
            options.Messages.Add(new ChatMessage(message.Role, message.Content));
        }

        try
        {
            _logger.LogDebug("Requesting chat completion with {MessageCount} messages and system prompt length {SystemPromptLength}.", options.Messages.Count, request.SystemPrompt.Length);
            Response<ChatCompletions> response = await _client.GetChatCompletionsAsync(request.DeploymentName, options, cancellationToken).ConfigureAwait(false);
            var bestChoice = response.Value.Choices.FirstOrDefault();
            string content = bestChoice?.Message.Content?.Trim() ?? string.Empty;
            _logger.LogDebug("Received chat completion with length {Length} characters.", content.Length);
            return content;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure OpenAI chat completion failed with status {Status}.", ex.Status);
            throw new InvalidOperationException($"Azure OpenAI chat completion failed: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<float>> GenerateEmbeddingAsync(OpenAiEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.InputText))
        {
            _logger.LogWarning("Embedding request received with empty input for deployment {Deployment}. Returning placeholder vector.", request.DeploymentName);
            return Array.Empty<float>();
        }

        var options = new EmbeddingsOptions(request.InputText);

        try
        {
            _logger.LogDebug("Requesting embedding for input length {Length} characters.", request.InputText.Length);
            Response<Embeddings> response = await _client.GetEmbeddingsAsync(request.DeploymentName, options, cancellationToken).ConfigureAwait(false);
            if (response?.Value?.Data is null || response.Value.Data.Count == 0)
            {
                _logger.LogWarning("Embedding response contained no vectors for deployment {Deployment}.", request.DeploymentName);
                return Array.Empty<float>();
            }

            var embedding = response.Value.Data.FirstOrDefault();
            IReadOnlyList<float> vector = embedding?.Embedding?.ToArray() ?? Array.Empty<float>();
            _logger.LogDebug("Received embedding with {DimensionCount} dimensions.", vector.Count);
            return vector;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure OpenAI embedding generation failed with status {Status}.", ex.Status);
            throw new InvalidOperationException($"Azure OpenAI embedding generation failed: {ex.Message}", ex);
        }
    }
}
