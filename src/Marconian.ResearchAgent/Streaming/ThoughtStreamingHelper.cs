using System;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;

namespace Marconian.ResearchAgent.Streaming;

internal static class ThoughtStreamingHelper
{
    public static async Task<string> StreamCompletionAsync(
        IAzureOpenAiService openAiService,
        OpenAiChatRequest request,
        IThoughtEventPublisher thoughtPublisher,
        string phase,
        string actor,
        Guid? correlationId,
        ThoughtSeverity severity,
        CancellationToken cancellationToken,
        Func<string, string>? detailFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(openAiService);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(thoughtPublisher);

        if (!thoughtPublisher.IsEnabled)
        {
            return await openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        }

        string formatDetail(string raw)
            => detailFormatter is null ? raw : detailFormatter(raw);

        using var buffer = new StreamingThoughtBuffer(detail =>
        {
            string formatted = formatDetail(detail);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                thoughtPublisher.Publish(phase, actor, formatted, severity, correlationId);
            }
        });

        string aggregated = await openAiService.StreamCompletionAsync(
            request,
            chunk => buffer.AppendAsync(chunk, cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);

        buffer.Flush();

        return string.IsNullOrWhiteSpace(aggregated)
            ? await openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false)
            : aggregated;
    }
}
