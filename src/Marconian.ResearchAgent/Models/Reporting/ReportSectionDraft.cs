using System;
using System.Collections.Generic;

namespace Marconian.ResearchAgent.Models.Reporting;

public sealed class ReportSectionDraft
{
    public required string SectionId { get; init; }

    public required string Title { get; init; }

    public required string Content { get; set; }

    public List<SourceCitation> Citations { get; init; } = new();

    public Dictionary<string, SourceCitation> CitationTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
