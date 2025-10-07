using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Microsoft.Extensions.Logging;

namespace Marconian.ResearchAgent.Streaming;

public sealed class ConsoleSummarizerAgent : IConsoleStreamSummarizer
{
    private const string SystemPrompt = "You summarize internal AI agent updates for an engineering console. Respond with a concise paragraph or up to four bullet points highlighting progress, decisions, and warnings. Avoid repetition.";

    private readonly IAzureOpenAiService _openAiService;
    private readonly string _deploymentName;
    private readonly ConsoleStreamingOptions _options;
    private readonly ILogger<ConsoleSummarizerAgent> _logger;
    private readonly IConsoleStreamSummarizer _fallback;
    private readonly object _gate = new();
    private DateTimeOffset _lastInvocationUtc = DateTimeOffset.MinValue;

    public ConsoleSummarizerAgent(
        IAzureOpenAiService openAiService,
        string deploymentName,
        ConsoleStreamingOptions options,
        ILogger<ConsoleSummarizerAgent> logger)
    {
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _deploymentName = string.IsNullOrWhiteSpace(deploymentName)
            ? throw new ArgumentException("Deployment name must be provided.", nameof(deploymentName))
            : deploymentName;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fallback = new DefaultConsoleStreamSummarizer();
    }

    public async Task<string?> SummarizeAsync(IReadOnlyList<ThoughtEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return null;
        }

        if (!_options.UseSummarizer || !_options.UseLlmSummarizer)
        {
            return await _fallback.SummarizeAsync(events, cancellationToken).ConfigureAwait(false);
        }

        bool shouldThrottle = false;
        if (_options.SummarizerMinInterval > TimeSpan.Zero)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                if (now - _lastInvocationUtc < _options.SummarizerMinInterval)
                {
                    shouldThrottle = true;
                }
                else
                {
                    _lastInvocationUtc = now;
                }
            }

            if (shouldThrottle)
            {
                return await _fallback.SummarizeAsync(events, cancellationToken).ConfigureAwait(false);
            }
        }

        string prompt = BuildPrompt(events);
        var request = new OpenAiChatRequest(
            SystemPrompt,
            new[] { new OpenAiChatMessage("user", prompt) },
            _deploymentName,
            MaxOutputTokens: _options.SummarizerMaxTokens > 0 ? _options.SummarizerMaxTokens : 200,
            Temperature: 0.4f,
            TopP: 0.95f);

        try
        {
            string summary = await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(summary))
            {
                return await _fallback.SummarizeAsync(events, cancellationToken).ConfigureAwait(false);
            }

            return summary.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Console LLM summarizer failed; using fallback summary.");
            return await _fallback.SummarizeAsync(events, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildPrompt(IReadOnlyList<ThoughtEvent> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Summarize the following internal updates for real-time console output.");
        builder.AppendLine("Highlight progress, blockers, and warnings in natural language.");
        builder.AppendLine();

        for (int index = 0; index < events.Count; index++)
        {
            ThoughtEvent evt = events[index];
            builder.Append("[ ")
                .Append(evt.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(evt.Phase)
                .Append(" | ")
                .Append(evt.Actor)
                .Append(" | ")
                .Append(evt.Severity)
                .Append(" ] ")
                .AppendLine(evt.Detail);
        }

        builder.AppendLine();
        builder.AppendLine("Respond with a short paragraph or bullet list. Do not invent details.");
        return builder.ToString();
    }
}
