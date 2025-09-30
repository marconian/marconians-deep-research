using System;
using System.Collections.Generic;
using Marconian.ResearchAgent.Models.Reporting;

namespace Marconian.ResearchAgent.Synthesis;

public sealed class ResearchAggregationResult
{
    public IReadOnlyList<ResearchFinding> Findings { get; init; } = Array.Empty<ResearchFinding>();

    public IReadOnlyList<SourceCitation> UniqueCitations { get; init; } = Array.Empty<SourceCitation>();
}


