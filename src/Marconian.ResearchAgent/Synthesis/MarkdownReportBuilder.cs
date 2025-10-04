using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Marconian.ResearchAgent.Models.Reporting;

namespace Marconian.ResearchAgent.Synthesis;

public sealed class MarkdownReportBuilder
{
    public string Build(
        string rootQuestion,
        string executiveSummary,
        ReportOutline outline,
        IReadOnlyList<ReportSectionDraft> sections,
        IReadOnlyList<ResearchFinding> findings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootQuestion);
        executiveSummary ??= string.Empty;
        ArgumentNullException.ThrowIfNull(outline);
        sections ??= Array.Empty<ReportSectionDraft>();
        findings ??= Array.Empty<ResearchFinding>();

        var draftMap = sections.ToDictionary(section => section.SectionId, StringComparer.OrdinalIgnoreCase);
        var planMap = outline.Sections.ToDictionary(section => section.SectionId, StringComparer.OrdinalIgnoreCase);

        var citationOrder = new List<SourceCitation>();
        var citationIndexMap = new Dictionary<string, int>(StringComparer.Ordinal);
        bool sourcesRendered = false;

        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(outline.Notes))
        {
            builder.AppendLine($"> {outline.Notes.Trim()}");
            builder.AppendLine();
        }

        if (outline.Layout.Count == 0)
        {
            RenderFallbackReport(builder, rootQuestion, executiveSummary, draftMap.Values, findings, citationOrder, citationIndexMap);
            sourcesRendered = true;
        }
        else
        {
            bool hasHeadingLevelOne = outline.Layout.Any(node => string.Equals(node.HeadingType, "h1", StringComparison.OrdinalIgnoreCase));
            if (!hasHeadingLevelOne)
            {
                builder.AppendLine($"# {rootQuestion}");
                builder.AppendLine();
            }

            foreach (var node in outline.Layout)
            {
                RenderLayoutNode(
                    node,
                    builder,
                    draftMap,
                    planMap,
                    findings,
                    executiveSummary,
                    citationOrder,
                    citationIndexMap,
                    ref sourcesRendered);
            }
        }

        if (!sourcesRendered && citationOrder.Count > 0)
        {
            builder.AppendLine("## Sources");
            builder.AppendLine();
            RegisterCitations(findings.SelectMany(f => f.Citations), citationOrder, citationIndexMap);
            AppendCitations(builder, citationOrder, citationIndexMap);
        }

        return builder.ToString();
    }

    private static void RenderLayoutNode(
        ReportLayoutNode node,
        StringBuilder builder,
        IReadOnlyDictionary<string, ReportSectionDraft> draftMap,
        IReadOnlyDictionary<string, ReportSectionPlan> planMap,
        IReadOnlyList<ResearchFinding> findings,
        string executiveSummary,
        List<SourceCitation> citationOrder,
        Dictionary<string, int> citationIndexMap,
        ref bool sourcesRendered)
    {
        if (string.IsNullOrWhiteSpace(node.Title))
        {
            return;
        }

        string heading = BuildHeading(node.HeadingType, node.Title);
        if (!string.IsNullOrEmpty(heading))
        {
            builder.AppendLine(heading);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(node.SectionId) &&
            draftMap.TryGetValue(node.SectionId, out var draft))
        {
            string trimmed = draft.Content.Trim();
            if (trimmed.Length > 0)
            {
                builder.AppendLine(trimmed);
                builder.AppendLine();
            }

            RegisterCitations(draft.Citations, citationOrder, citationIndexMap);
        }
        else if (!string.IsNullOrWhiteSpace(node.SectionId) &&
                 planMap.TryGetValue(node.SectionId, out var plan) &&
                 !string.IsNullOrWhiteSpace(plan.Summary))
        {
            builder.AppendLine(plan.Summary.Trim());
            builder.AppendLine();
        }
        else if (string.IsNullOrWhiteSpace(node.SectionId) &&
                 string.Equals(node.Title, "Executive Summary", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(executiveSummary))
        {
            builder.AppendLine(executiveSummary.Trim());
            builder.AppendLine();
        }
        else if (string.IsNullOrWhiteSpace(node.SectionId) &&
                 string.Equals(node.Title, "Sources", StringComparison.OrdinalIgnoreCase))
        {
            RegisterCitations(findings.SelectMany(f => f.Citations), citationOrder, citationIndexMap);
            AppendCitations(builder, citationOrder, citationIndexMap);
            builder.AppendLine();
            sourcesRendered = true;
        }

        foreach (var child in node.Children)
        {
            RenderLayoutNode(
                child,
                builder,
                draftMap,
                planMap,
                findings,
                executiveSummary,
                citationOrder,
                citationIndexMap,
                ref sourcesRendered);
        }
    }

    private static string BuildHeading(string? headingType, string title)
    {
        string marker = headingType?.Trim().ToLowerInvariant() switch
        {
            "h1" or "#" => "#",
            "h2" or "##" => "##",
            "h3" or "###" => "###",
            "h4" or "####" => "####",
            _ => "##"
        };

        return $"{marker} {title.Trim()}";
    }

    private static void RegisterCitations(
        IEnumerable<SourceCitation> citations,
        List<SourceCitation> citationOrder,
        Dictionary<string, int> citationIndexMap)
    {
        foreach (var citation in citations)
        {
            if (citation is null)
            {
                continue;
            }

            if (citationIndexMap.ContainsKey(citation.SourceId))
            {
                continue;
            }

            citationOrder.Add(citation);
            citationIndexMap[citation.SourceId] = citationOrder.Count;
        }
    }

    private static void AppendCitations(
        StringBuilder builder,
        List<SourceCitation> citationOrder,
        Dictionary<string, int> citationIndexMap)
    {
        foreach (var citation in citationOrder)
        {
            if (!citationIndexMap.TryGetValue(citation.SourceId, out int index))
            {
                continue;
            }

            string displayTitle = string.IsNullOrWhiteSpace(citation.Title) ? citation.SourceId : citation.Title!.Trim();
            string displayUrl = string.IsNullOrWhiteSpace(citation.Url) ? string.Empty : citation.Url.Trim();
            string snippet = string.IsNullOrWhiteSpace(citation.Snippet) ? string.Empty : citation.Snippet.Trim();

            var parts = new List<string> { displayTitle };
            if (!string.IsNullOrEmpty(displayUrl))
            {
                parts.Add(displayUrl);
            }

            if (!string.IsNullOrEmpty(snippet))
            {
                parts.Add(snippet);
            }

            builder.AppendLine($"[{index}] {string.Join(" – ", parts)}");
        }
    }

    private static void RenderFallbackReport(
        StringBuilder builder,
        string rootQuestion,
        string executiveSummary,
        IEnumerable<ReportSectionDraft> drafts,
        IReadOnlyList<ResearchFinding> findings,
        List<SourceCitation> citationOrder,
        Dictionary<string, int> citationIndexMap)
    {
        builder.AppendLine($"# {rootQuestion}");
        builder.AppendLine();

        builder.AppendLine("## Executive Summary");
        if (string.IsNullOrWhiteSpace(executiveSummary))
        {
            builder.AppendLine("(No summary provided.)");
        }
        else
        {
            builder.AppendLine(executiveSummary.Trim());
        }
        builder.AppendLine();

        foreach (var draft in drafts)
        {
            builder.AppendLine($"## {draft.Title}");
            builder.AppendLine(draft.Content.Trim());
            builder.AppendLine();

            RegisterCitations(draft.Citations, citationOrder, citationIndexMap);
        }

        RegisterCitations(findings.SelectMany(f => f.Citations), citationOrder, citationIndexMap);
        builder.AppendLine("## Sources");
        builder.AppendLine();
        AppendCitations(builder, citationOrder, citationIndexMap);
    }
}


