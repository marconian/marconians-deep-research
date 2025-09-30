namespace Marconian.ResearchAgent.Models.Reporting;

public sealed class ResearchFinding
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Title { get; set; }

    public required string Content { get; set; }

    public List<SourceCitation> Citations { get; set; } = new();

    public double Confidence { get; set; } = 0.5d;
}

public sealed record SourceCitation(string SourceId, string? Title, string? Url, string? Snippet);

