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

        var sectionMap = sections.ToDictionary(section => section.SectionId, StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        builder.AppendLine($"# Research Report: {rootQuestion}");
        builder.AppendLine();
        builder.AppendLine("## Executive Summary");
        builder.AppendLine(executiveSummary.Trim().Length == 0 ? "(No summary produced.)" : executiveSummary.Trim());
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(outline.Notes) || outline.CoreSections.Count > 0 || outline.GeneralSections.Count > 0)
        {
            builder.AppendLine("## Outline Snapshot");
            if (!string.IsNullOrWhiteSpace(outline.Notes))
            {
                builder.AppendLine(outline.Notes!.Trim());
            }

            foreach (var section in outline.CoreSections)
            {
                builder.AppendLine($"- Core: {section.Title} {(string.IsNullOrWhiteSpace(section.Summary) ? string.Empty : $"- {section.Summary!.Trim()}")}");
            }

            foreach (var section in outline.GeneralSections)
            {
                builder.AppendLine($"- General: {section.Title} {(string.IsNullOrWhiteSpace(section.Summary) ? string.Empty : $"- {section.Summary!.Trim()}")}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Detailed Sections");

        int citationIndex = 1;
        var citationMap = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var plan in outline.CoreSections)
        {
            if (!sectionMap.TryGetValue(plan.SectionId, out var draft))
            {
                continue;
            }

            AppendSection(builder, draft, citationMap, ref citationIndex);
        }

        if (outline.GeneralSections.Count > 0)
        {
            builder.AppendLine("## General Sections");
            builder.AppendLine();

            foreach (var plan in outline.GeneralSections)
            {
                if (!sectionMap.TryGetValue(plan.SectionId, out var draft))
                {
                    continue;
                }

                AppendSection(builder, draft, citationMap, ref citationIndex);
            }
        }

        if (findings.Count > 0)
        {
            builder.AppendLine("## Appendix: Findings Reference");
            foreach (var finding in findings)
            {
                builder.AppendLine($"### {finding.Title}");
                builder.AppendLine(finding.Content);
                builder.AppendLine();
            }
        }

        if (citationMap.Count > 0)
        {
            builder.AppendLine("## Citation Index");
            foreach (var kvp in citationMap.OrderBy(k => k.Value))
            {
                builder.AppendLine($"[{kvp.Value}] {kvp.Key}");
            }
        }

        return builder.ToString();
    }

    private static void AppendSection(
        StringBuilder builder,
        ReportSectionDraft draft,
        Dictionary<string, int> citationMap,
        ref int citationIndex)
    {
        builder.AppendLine($"### {draft.Title}");
        builder.AppendLine(draft.Content.Trim());
        builder.AppendLine();

        if (draft.Citations.Count == 0)
        {
            return;
        }

        builder.AppendLine("**Citations:**");
        foreach (var citation in draft.Citations)
        {
            if (!citationMap.TryGetValue(citation.SourceId, out var index))
            {
                index = citationIndex++;
                citationMap[citation.SourceId] = index;
            }

            builder.AppendLine($"- [{index}] {citation.Title ?? citation.SourceId} - {citation.Url}");
        }

        builder.AppendLine();
    }
}


