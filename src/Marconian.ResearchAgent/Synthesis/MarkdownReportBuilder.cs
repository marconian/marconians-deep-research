using System;
using System.Collections.Generic;
using System.Text;
using Marconian.ResearchAgent.Models.Reporting;

namespace Marconian.ResearchAgent.Synthesis;

public sealed class MarkdownReportBuilder
{
    public string Build(string rootQuestion, string executiveSummary, IReadOnlyList<ResearchFinding> findings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootQuestion);
        executiveSummary ??= string.Empty;
        findings ??= Array.Empty<ResearchFinding>();

        var builder = new StringBuilder();
        builder.AppendLine($"# Research Report: {rootQuestion}");
        builder.AppendLine();
        builder.AppendLine("## Executive Summary");
        builder.AppendLine(executiveSummary.Trim().Length == 0 ? "(No summary produced.)" : executiveSummary.Trim());
        builder.AppendLine();
        builder.AppendLine("## Detailed Findings");

        int citationIndex = 1;
        var citationMap = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            builder.AppendLine($"### {finding.Title}");
            builder.AppendLine(finding.Content);
            builder.AppendLine();

            if (finding.Citations.Count == 0)
            {
                continue;
            }

            builder.AppendLine("**Citations:**");
            foreach (var citation in finding.Citations)
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
}


