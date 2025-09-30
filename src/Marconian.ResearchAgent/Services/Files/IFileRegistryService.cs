using Marconian.ResearchAgent.Models.Files;

namespace Marconian.ResearchAgent.Services.Files;

public interface IFileRegistryService
{
    Task<FileRegistryEntry> SaveFileAsync(
        string researchSessionId,
        string originalFileName,
        Stream content,
        string? contentType,
        string? sourceUrl,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenFileAsync(string researchSessionId, string fileId, CancellationToken cancellationToken = default);

    Task<FileRegistryEntry?> GetEntryAsync(string researchSessionId, string fileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileRegistryEntry>> ListEntriesAsync(string researchSessionId, CancellationToken cancellationToken = default);
}
