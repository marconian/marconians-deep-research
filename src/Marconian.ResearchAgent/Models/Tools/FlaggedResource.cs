namespace Marconian.ResearchAgent.Models.Tools;

public enum FlaggedResourceType
{
    Page,
    File,
    Download
}

public sealed record FlaggedResource(
    FlaggedResourceType Type,
    string Title,
    string Url,
    string? MimeType = null,
    string? Notes = null);
