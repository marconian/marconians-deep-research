using System;
using System.Collections.Generic;

namespace Marconian.ResearchAgent.Models.Reporting;

public sealed class ReportOutline
{
    public string OutlineId { get; init; } = Guid.NewGuid().ToString("N");

    public required string Objective { get; init; }

    public string? Notes { get; set; }

    public List<ReportSectionPlan> CoreSections { get; init; } = new();

    public List<ReportSectionPlan> GeneralSections { get; init; } = new();
}

public sealed class ReportSectionPlan
{
    public string SectionId { get; init; } = Guid.NewGuid().ToString("N");

    public required string Title { get; init; }

    public string? Summary { get; set; }

    public List<string> SupportingFindingIds { get; init; } = new();
}
