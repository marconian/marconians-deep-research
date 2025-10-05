using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Marconian.ResearchAgent.Models.Reporting;

namespace Marconian.ResearchAgent.Synthesis;

public sealed class MarkdownReportBuilder
{
    private static readonly Regex CitationTagRegex = new("<<ref:(?<tag>[A-Za-z0-9_-]+)>>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceCollapseRegex = new(@"\s+", RegexOptions.Compiled);

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

        string reportTitle = ResolveReportTitle(outline, rootQuestion);
        builder.AppendLine($"# {reportTitle}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(outline.Notes))
        {
            builder.AppendLine($"> {outline.Notes.Trim()}");
            builder.AppendLine();
        }

        if (outline.Layout.Count == 0)
        {
            sourcesRendered = RenderFallbackReport(
                builder,
                executiveSummary,
                draftMap.Values,
                citationOrder,
                citationIndexMap);
        }
        else
        {
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
            AppendCitations(builder, citationOrder, citationIndexMap);
        }

        return builder.ToString();
    }

    public string BuildSourcesSection(IEnumerable<SourceCitation> citations, bool includeHeading = true)
    {
        var citationOrder = new List<SourceCitation>();
        var citationIndexMap = new Dictionary<string, int>(StringComparer.Ordinal);

        if (citations is not null)
        {
            foreach (var citation in citations)
            {
                if (citation is null)
                {
                    continue;
                }

                EnsureCitationRegistered(citation, citationOrder, citationIndexMap);
            }
        }

        if (citationOrder.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        if (includeHeading)
        {
            builder.AppendLine("## Sources");
            builder.AppendLine();
        }

        AppendCitations(builder, citationOrder, citationIndexMap);
        return builder.ToString().TrimEnd();
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
            string processed = ReplaceCitationTags(draft.Content, draft, citationOrder, citationIndexMap, out bool tagsApplied);
            if (processed.Length > 0)
            {
                builder.AppendLine(processed);
                builder.AppendLine();
            }

            if (!tagsApplied)
            {
                RegisterCitations(draft.Citations, citationOrder, citationIndexMap);
            }
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
            if (citationOrder.Count > 0)
            {
                AppendCitations(builder, citationOrder, citationIndexMap);
                builder.AppendLine();
            }
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
            "h3" or "###" => "###",
            "h4" or "####" => "####",
            "h2" or "##" => "##",
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

            EnsureCitationRegistered(citation, citationOrder, citationIndexMap);
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

            string displayTitle = NormalizeWhitespaceOrFallback(citation.Title, citation.SourceId);
            string displayUrl = NormalizeWhitespace(citation.Url);
            string snippet = NormalizeWhitespace(citation.Snippet);

            var parts = new List<string> { displayTitle };
            if (!string.IsNullOrEmpty(displayUrl))
            {
                parts.Add(displayUrl);
            }

            if (!string.IsNullOrEmpty(snippet))
            {
                parts.Add(snippet);
            }

            builder.AppendLine($"[^{index}]: {string.Join(" – ", parts)}");
        }
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string collapsed = WhitespaceCollapseRegex.Replace(text, " ");
        return collapsed.Trim();
    }

    private static string NormalizeWhitespaceOrFallback(string? text, string fallback)
    {
        string normalized = NormalizeWhitespace(text);
        return string.IsNullOrEmpty(normalized) ? fallback : normalized;
    }

    private static string ReplaceCitationTags(
        string content,
        ReportSectionDraft draft,
        List<SourceCitation> citationOrder,
        Dictionary<string, int> citationIndexMap,
        out bool replacementsMade)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            replacementsMade = false;
            return string.Empty;
        }

        if (draft.CitationTags.Count == 0)
        {
            replacementsMade = false;
            return content.Trim();
        }

        bool anyReplacement = false;
        string replaced = CitationTagRegex.Replace(content, match =>
        {
            string tag = match.Groups["tag"].Value;
            if (!draft.CitationTags.TryGetValue(tag, out var citation))
            {
                return string.Empty;
            }

            int index = EnsureCitationRegistered(citation, citationOrder, citationIndexMap);
            anyReplacement = true;
            return $"[^{index}]";
        });

        replacementsMade = anyReplacement;
        return replaced.Trim();
    }

    private static int EnsureCitationRegistered(
        SourceCitation citation,
        List<SourceCitation> citationOrder,
        Dictionary<string, int> citationIndexMap)
    {
        if (citationIndexMap.TryGetValue(citation.SourceId, out int existing))
        {
            return existing;
        }

        citationOrder.Add(citation);
        int index = citationOrder.Count;
        citationIndexMap[citation.SourceId] = index;
        return index;
    }

    private static bool RenderFallbackReport(
        StringBuilder builder,
        string executiveSummary,
        IEnumerable<ReportSectionDraft> drafts,
        List<SourceCitation> citationOrder,
        Dictionary<string, int> citationIndexMap)
    {
        if (!string.IsNullOrWhiteSpace(executiveSummary))
        {
            builder.AppendLine("## Executive Summary");
            builder.AppendLine(executiveSummary.Trim());
            builder.AppendLine();
        }

        foreach (var draft in drafts)
        {
            builder.AppendLine($"## {draft.Title}");
            string processed = ReplaceCitationTags(draft.Content, draft, citationOrder, citationIndexMap, out bool tagsApplied);
            builder.AppendLine(processed);
            builder.AppendLine();

            if (!tagsApplied)
            {
                RegisterCitations(draft.Citations, citationOrder, citationIndexMap);
            }
        }

        if (citationOrder.Count == 0)
        {
            return false;
        }

        builder.AppendLine("## Sources");
        builder.AppendLine();
        AppendCitations(builder, citationOrder, citationIndexMap);
        return true;
    }

    private static string ResolveReportTitle(ReportOutline outline, string rootQuestion)
    {
        if (!string.IsNullOrWhiteSpace(outline.Title))
        {
            return outline.Title!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(rootQuestion))
        {
            return rootQuestion.Trim();
        }

        return "Autonomous Research Report";
    }
}


