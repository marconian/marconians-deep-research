using System.Linq;
using Azure;
using Azure.AI.OpenAI;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Services.OpenAI.Models;

namespace Marconian.ResearchAgent.Services.OpenAI;

public sealed class AzureOpenAiService : IAzureOpenAiService
{
    private readonly OpenAIClient _client;

    public AzureOpenAiService(Settings.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _client = new OpenAIClient(new Uri(settings.AzureOpenAiEndpoint), new AzureKeyCredential(settings.AzureOpenAiApiKey));
    }

    public async Task<string> GenerateTextAsync(OpenAiChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new ChatCompletionsOptions
        {
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature
        };

        options.Messages.Add(new ChatMessage(ChatRole.System, request.SystemPrompt));
        foreach (var message in request.Messages)
        {
            options.Messages.Add(new ChatMessage(message.Role, message.Content));
        }

        try
        {
            Response<ChatCompletions> response = await _client.GetChatCompletionsAsync(request.DeploymentName, options, cancellationToken).ConfigureAwait(false);
            var bestChoice = response.Value.Choices.FirstOrDefault();
            return bestChoice?.Message.Content?.Trim() ?? string.Empty;
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Azure OpenAI chat completion failed: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<float>> GenerateEmbeddingAsync(OpenAiEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new EmbeddingsOptions
        {
            Input = { request.InputText }
        };

        try
        {
            Response<Embeddings> response = await _client.GetEmbeddingsAsync(request.DeploymentName, options, cancellationToken).ConfigureAwait(false);
            var embedding = response.Value.Data.FirstOrDefault();
            return embedding?.Embedding.ToArray() ?? Array.Empty<float>();
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Azure OpenAI embedding generation failed: {ex.Message}", ex);
        }
    }
}




