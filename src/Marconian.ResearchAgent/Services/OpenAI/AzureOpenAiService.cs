using System.Linq;
using System.Text;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.ClientModel;

#pragma warning disable OPENAI001
namespace Marconian.ResearchAgent.Services.OpenAI;

public sealed class AzureOpenAiService : IAzureOpenAiService
{
    private readonly ChatClient _chatClient;
    private readonly EmbeddingClient _embeddingsClient;
    private readonly string _chatDeploymentName;
    private readonly string _embeddingDeploymentName;
    private readonly ILogger<AzureOpenAiService> _logger;
    private readonly ChatReasoningEffortLevel? _reasoningEffortLevel;

    public AzureOpenAiService(Settings.AppSettings settings, ILogger<AzureOpenAiService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? NullLogger<AzureOpenAiService>.Instance;
        var credential = new ApiKeyCredential(settings.AzureOpenAiApiKey);
        var client = new AzureOpenAIClient(new Uri(settings.AzureOpenAiEndpoint), credential);
        _chatDeploymentName = settings.AzureOpenAiChatDeployment;
        _embeddingDeploymentName = settings.AzureOpenAiEmbeddingDeployment;
        _chatClient = client.GetChatClient(_chatDeploymentName);
        _embeddingsClient = client.GetEmbeddingClient(_embeddingDeploymentName);

        if (!string.IsNullOrWhiteSpace(settings.AzureOpenAiReasoningEffortLevel))
        {
            if (Enum.TryParse(settings.AzureOpenAiReasoningEffortLevel, ignoreCase: true, out ChatReasoningEffortLevel parsedLevel))
            {
                _reasoningEffortLevel = parsedLevel;
            }
            else
            {
                _logger.LogWarning("Unknown Azure OpenAI reasoning effort level '{ReasoningEffortLevel}'. Falling back to service default.", settings.AzureOpenAiReasoningEffortLevel);
            }
        }
    }

    public async Task<string> GenerateTextAsync(OpenAiChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(request.SystemPrompt)
        };

        foreach (var message in request.Messages)
        {
            messages.Add(ToChatMessage(message));
        }

        var options = new ChatCompletionOptions();

        // Azure's latest models reject the legacy max_tokens parameter. Until the SDK exposes the
        // replacement field, skip sending an explicit cap but keep a debug trace for visibility.
        if (request.MaxOutputTokens > 0)
        {
            _logger.LogDebug("MaxOutputTokens requested ({MaxTokens}), but current Azure OpenAI deployment does not support explicit max token parameters. Skipping explicit limit.", request.MaxOutputTokens);
        }

        if (request.Temperature is float temperature)
        {
            options.Temperature = temperature;
        }

        if (request.TopP is float topP)
        {
            options.TopP = topP;
        }

        if (request.JsonSchemaFormat is not null)
        {
            options.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                request.JsonSchemaFormat.SchemaName,
                BinaryData.FromString(request.JsonSchemaFormat.Schema.ToJsonString()),
                jsonSchemaIsStrict: request.JsonSchemaFormat.Strict);
        }

        if (_reasoningEffortLevel is { } configuredEffortLevel)
        {
            options.ReasoningEffortLevel = configuredEffortLevel;
        }

        try
        {
            _logger.LogDebug("Requesting chat completion with {MessageCount} messages and system prompt length {SystemPromptLength}.", messages.Count, request.SystemPrompt.Length);
            ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options, cancellationToken).ConfigureAwait(false);

            string content = ExtractCompletionText(completion);
            _logger.LogDebug("Received chat completion with length {Length} characters.", content.Length);
            return content;
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException)
        {
            _logger.LogError(ex, "Azure OpenAI chat completion failed for deployment {Deployment}.", _chatDeploymentName);
            throw new InvalidOperationException($"Azure OpenAI chat completion failed: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<float>> GenerateEmbeddingAsync(OpenAiEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.InputText))
        {
            _logger.LogWarning("Embedding request received with empty input for deployment {Deployment}. Returning placeholder vector.", _embeddingDeploymentName);
            return Array.Empty<float>();
        }

        try
        {
            _logger.LogDebug("Requesting embedding for input length {Length} characters.", request.InputText.Length);
            ClientResult<OpenAIEmbeddingCollection> embeddings = await _embeddingsClient.GenerateEmbeddingsAsync([request.InputText], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (embeddings.Value.FirstOrDefault() is not { } embedding)
            {
                _logger.LogWarning("Embedding response contained no vectors for deployment {Deployment}.", _embeddingDeploymentName);
                return Array.Empty<float>();
            }

            IReadOnlyList<float> vector = embedding.ToFloats().ToArray() ?? Array.Empty<float>();
            _logger.LogDebug("Received embedding with {DimensionCount} dimensions.", vector.Count);
            return vector;
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException)
        {
            _logger.LogError(ex, "Azure OpenAI embedding generation failed for deployment {Deployment}.", _embeddingDeploymentName);
            throw new InvalidOperationException($"Azure OpenAI embedding generation failed: {ex.Message}", ex);
        }
    }

    private static ChatMessage ToChatMessage(OpenAiChatMessage message)
    {
        string role = message.Role?.Trim().ToLowerInvariant() ?? string.Empty;
        return role switch
        {
            "system" => new SystemChatMessage(message.Content),
            "assistant" => new AssistantChatMessage(message.Content),
            "tool" => new ToolChatMessage("tool", message.Content),
            _ => new UserChatMessage(message.Content)
        };
    }

    private static string ExtractCompletionText(ChatCompletion completion)
    {
        if (completion is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var part in completion.Content)
        {
            if (part.Text is not null)
            {
                builder.Append(part.Text);
            }
        }

        return builder.ToString().Trim();
    }
}
#pragma warning restore OPENAI001
