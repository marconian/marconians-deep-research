using System;
using System.Linq;
using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Models.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Services.Files;

public sealed class DocumentIntelligenceService : IDocumentIntelligenceService
{
    private readonly DocumentIntelligenceClient _client;
    private readonly ILogger<DocumentIntelligenceService> _logger;

    public DocumentIntelligenceService(Settings.AppSettings settings, ILogger<DocumentIntelligenceService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? NullLogger<DocumentIntelligenceService>.Instance;
        _client = new DocumentIntelligenceClient(new Uri(settings.CognitiveServicesEndpoint), new AzureKeyCredential(settings.CognitiveServicesApiKey));
    }

    public async Task<DocumentAnalysisResult> AnalyzeDocumentAsync(Stream documentStream, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentStream);
        BinaryData content = await BinaryData.FromStreamAsync(documentStream, cancellationToken).ConfigureAwait(false);
        documentStream.Position = 0;

        try
        {
            var operation = await _client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", content, cancellationToken: cancellationToken).ConfigureAwait(false);
            var analysis = operation.Value;

            var builder = new StringBuilder();
            if (analysis.Paragraphs is not null)
            {
                foreach (var paragraph in analysis.Paragraphs)
                {
                    if (!string.IsNullOrWhiteSpace(paragraph.Content))
                    {
                        builder.AppendLine(paragraph.Content.Trim());
                    }
                }
            }

            var result = new DocumentAnalysisResult
            {
                Text = builder.ToString().Trim(),
                Metadata =
                {
                    ["contentType"] = contentType
                }
            };

            if (analysis.Paragraphs?.FirstOrDefault(p => string.Equals(p.Role?.ToString(), "title", StringComparison.OrdinalIgnoreCase)) is { } titleParagraph)
            {
                result.Title = titleParagraph.Content;
            }

            if (analysis.KeyValuePairs is not null)
            {
                foreach (var kvp in analysis.KeyValuePairs)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Value?.Content) && !string.IsNullOrWhiteSpace(kvp.Key?.Content))
                    {
                        result.Metadata[kvp.Key.Content] = kvp.Value.Content;
                    }
                }
            }

            if (analysis.Tables is not null)
            {
                result.Metadata["tableCount"] = analysis.Tables.Count.ToString();
            }

            _logger.LogDebug("Document analysis produced {CharacterCount} characters of text.", result.Text.Length);
            return result;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Document Intelligence request failed.");
            throw new InvalidOperationException($"Document analysis failed: {ex.Message}", ex);
        }
    }
}
