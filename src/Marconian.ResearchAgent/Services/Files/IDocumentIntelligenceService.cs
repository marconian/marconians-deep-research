using Marconian.ResearchAgent.Models.Files;

namespace Marconian.ResearchAgent.Services.Files;

public interface IDocumentIntelligenceService
{
    Task<DocumentAnalysisResult> AnalyzeDocumentAsync(Stream documentStream, string contentType, CancellationToken cancellationToken = default);
}
