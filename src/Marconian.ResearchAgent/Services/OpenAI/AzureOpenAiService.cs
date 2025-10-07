using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private readonly AzureOpenAIClient _client;
    private readonly ConcurrentDictionary<string, ChatClient> _chatClients = new();
    private readonly EmbeddingClient _embeddingsClient;
    private readonly string _chatDeploymentName;
    private readonly string _embeddingDeploymentName;
    private readonly ILogger<AzureOpenAiService> _logger;
    private readonly IReadOnlyDictionary<string, ChatReasoningEffortLevel?> _reasoningEffortByDeployment;
    private readonly ChatReasoningEffortLevel? _defaultReasoningEffortLevel;

    public AzureOpenAiService(Settings.AppSettings settings, ILogger<AzureOpenAiService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? NullLogger<AzureOpenAiService>.Instance;
    var credential = new ApiKeyCredential(settings.AzureOpenAiApiKey);
    _client = new AzureOpenAIClient(new Uri(settings.AzureOpenAiEndpoint), credential);
        _chatDeploymentName = settings.AzureOpenAiChatDeployment;
        _embeddingDeploymentName = settings.AzureOpenAiEmbeddingDeployment;
    var defaultChatClient = _client.GetChatClient(_chatDeploymentName);
    _chatClients.TryAdd(_chatDeploymentName, defaultChatClient);
    _embeddingsClient = _client.GetEmbeddingClient(_embeddingDeploymentName);

        _reasoningEffortByDeployment = BuildReasoningEffortMap(settings);

        if (_reasoningEffortByDeployment.TryGetValue(_chatDeploymentName, out var stageEffort))
        {
            _defaultReasoningEffortLevel = stageEffort;
        }
        else if (!string.IsNullOrWhiteSpace(settings.AzureOpenAiReasoningEffortLevel) &&
                 TryResolveReasoningEffortLevel(settings.AzureOpenAiReasoningEffortLevel, out var parsedLevel))
        {
            _defaultReasoningEffortLevel = parsedLevel;
        }
    }

    public async Task<string> GenerateTextAsync(OpenAiChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

    var (messages, options) = PrepareChatRequest(request);
    string deployment = ResolveDeployment(request);
    ConfigureReasoning(options, deployment);
        ChatClient chatClient = GetChatClient(deployment);

        try
        {
            _logger.LogDebug("Requesting chat completion with {MessageCount} messages and system prompt length {SystemPromptLength} for deployment {Deployment}.", messages.Count, request.SystemPrompt.Length, deployment);
            ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken).ConfigureAwait(false);

            string content = ExtractCompletionText(completion);
            _logger.LogDebug("Received chat completion with length {Length} characters.", content.Length);
            return content;
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException)
        {
            _logger.LogError(ex, "Azure OpenAI chat completion failed for deployment {Deployment}.", deployment);
            throw new InvalidOperationException($"Azure OpenAI chat completion failed: {ex.Message}", ex);
        }
    }

    public async Task<string> StreamCompletionAsync(OpenAiChatRequest request, Func<string, Task> onChunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onChunk);

    var (messages, options) = PrepareChatRequest(request);
    string deployment = ResolveDeployment(request);
    ConfigureReasoning(options, deployment);
        ChatClient chatClient = GetChatClient(deployment);

        try
        {
            _logger.LogDebug("Requesting streaming chat completion with {MessageCount} messages and system prompt length {SystemPromptLength} for deployment {Deployment}.", messages.Count, request.SystemPrompt.Length, deployment);

            var updates = chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken);
            var builder = new StringBuilder();

            await foreach (var update in updates.WithCancellation(cancellationToken))
            {
                foreach (string text in ExtractTextSegments(update))
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    builder.Append(text);
                    await onChunk(text).ConfigureAwait(false);
                }
            }

            string aggregated = builder.ToString().Trim();
            if (aggregated.Length == 0)
            {
                _logger.LogDebug("Streaming yielded no text segments; falling back to buffered completion for deployment {Deployment}.", deployment);
                string fallback = await GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    await onChunk(fallback).ConfigureAwait(false);
                }

                return fallback;
            }

            _logger.LogDebug("Streaming chat completion produced {Length} characters after aggregation.", aggregated.Length);
            return aggregated;
        }
        catch (Exception ex) when (IsStreamingFallbackCandidate(ex))
        {
            _logger.LogWarning(ex, "Streaming not available for deployment {Deployment}. Falling back to buffered completion.", deployment);
            string fallback = await GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                await onChunk(fallback).ConfigureAwait(false);
            }

            return fallback;
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

    private (List<ChatMessage> Messages, ChatCompletionOptions Options) PrepareChatRequest(OpenAiChatRequest request)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(request.SystemPrompt)
        };

        foreach (var message in request.Messages)
        {
            messages.Add(ToChatMessage(message));
        }

        var options = new ChatCompletionOptions();

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

        return (messages, options);
    }

    private string ResolveDeployment(OpenAiChatRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DeploymentName))
        {
            return _chatDeploymentName;
        }

        return request.DeploymentName;
    }

    private ChatClient GetChatClient(string deploymentName)
    {
        string resolved = string.IsNullOrWhiteSpace(deploymentName)
            ? _chatDeploymentName
            : deploymentName;

        return _chatClients.GetOrAdd(resolved, name =>
        {
            _logger.LogDebug("Creating chat client for deployment {Deployment}.", name);
            return _client.GetChatClient(name);
        });
    }

    private static IEnumerable<string> ExtractTextSegments(object? update)
    {
        if (update is null)
        {
            yield break;
        }

        var contentProperty = update.GetType().GetProperty("ContentUpdate");
        if (contentProperty?.GetValue(update) is not IEnumerable contentItems)
        {
            yield break;
        }

        foreach (var item in contentItems)
        {
            if (item is null)
            {
                continue;
            }

            var textProperty = item.GetType().GetProperty("Text");
            if (textProperty?.GetValue(item) is string text && !string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    private static bool IsStreamingFallbackCandidate(Exception ex)
        => ex is NotSupportedException
            || ex is InvalidOperationException
            || ex is RequestFailedException { Status: 404 or 409 or 501 };

    private static bool TryResolveReasoningEffortLevel(string value, out ChatReasoningEffortLevel effortLevel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            effortLevel = default;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "low":
                effortLevel = ChatReasoningEffortLevel.Low;
                return true;
            case "medium":
                effortLevel = ChatReasoningEffortLevel.Medium;
                return true;
            case "high":
                effortLevel = ChatReasoningEffortLevel.High;
                return true;
            default:
                effortLevel = default;
                return false;
        }
    }

    private void ConfigureReasoning(ChatCompletionOptions options, string deployment)
    {
        if (options is null)
        {
            return;
        }

        if (_reasoningEffortByDeployment.TryGetValue(deployment, out var stageEffortLevel))
        {
            if (stageEffortLevel.HasValue)
            {
                options.ReasoningEffortLevel = stageEffortLevel.Value;
            }

            return;
        }

        if (_defaultReasoningEffortLevel.HasValue &&
            string.Equals(deployment, _chatDeploymentName, StringComparison.OrdinalIgnoreCase))
        {
            options.ReasoningEffortLevel = _defaultReasoningEffortLevel.Value;
        }
    }

    private IReadOnlyDictionary<string, ChatReasoningEffortLevel?> BuildReasoningEffortMap(Settings.AppSettings settings)
    {
        if (settings.AzureOpenAiDeploymentReasoningLevels is null || settings.AzureOpenAiDeploymentReasoningLevels.Count == 0)
        {
            return new Dictionary<string, ChatReasoningEffortLevel?>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, ChatReasoningEffortLevel?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in settings.AzureOpenAiDeploymentReasoningLevels)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            if (entry.Value is null)
            {
                map[entry.Key] = null;
                continue;
            }

            if (TryResolveReasoningEffortLevel(entry.Value, out var parsedLevel))
            {
                map[entry.Key] = parsedLevel;
            }
            else
            {
                _logger.LogWarning(
                    "Unknown Azure OpenAI reasoning effort level '{ReasoningEffortLevel}' configured for deployment '{Deployment}'. Skipping explicit reasoning configuration.",
                    entry.Value,
                    entry.Key);
            }
        }

        return map;
    }
}
#pragma warning restore OPENAI001
