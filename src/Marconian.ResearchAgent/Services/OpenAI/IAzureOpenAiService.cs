using Marconian.ResearchAgent.Services.OpenAI.Models;

namespace Marconian.ResearchAgent.Services.OpenAI;

public interface IAzureOpenAiService
{
    Task<string> GenerateTextAsync(OpenAiChatRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float>> GenerateEmbeddingAsync(OpenAiEmbeddingRequest request, CancellationToken cancellationToken = default);

    Task<string> StreamCompletionAsync(OpenAiChatRequest request, Func<string, Task> onChunk,
        CancellationToken cancellationToken = default);
}
