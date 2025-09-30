using System;
using System.Collections.Generic;
using System.Linq;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Research;

namespace Marconian.ResearchAgent.Synthesis;

public sealed class ResearchAggregator
{
    public ResearchAggregationResult Aggregate(IEnumerable<ResearchBranchResult> branchResults)
    {
        ArgumentNullException.ThrowIfNull(branchResults);

        var findingMap = new Dictionary<string, ResearchFinding>(StringComparer.OrdinalIgnoreCase);

        foreach (var branch in branchResults)
        {
            if (branch?.Finding is not { } finding)
            {
                continue;
            }

            string key = NormalizeKey(finding.Title);
            if (!findingMap.TryGetValue(key, out var existing))
            {
                findingMap[key] = CloneFinding(finding);
                continue;
            }

            MergeFinding(existing, finding);
        }

        var mergedFindings = findingMap.Values
            .OrderByDescending(f => f.Confidence)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var uniqueCitations = mergedFindings
            .SelectMany(f => f.Citations)
            .Where(c => c is not null)
            .GroupBy(c => c.SourceId)
            .Select(group => group.First())
            .ToList();

        return new ResearchAggregationResult
        {
            Findings = mergedFindings,
            UniqueCitations = uniqueCitations
        };
    }

    private static string NormalizeKey(string value)
        => string.IsNullOrWhiteSpace(value) ? "(untitled)" : value.Trim();

    private static ResearchFinding CloneFinding(ResearchFinding finding)
        => new()
        {
            Id = finding.Id,
            Title = finding.Title,
            Content = finding.Content,
            Citations = finding.Citations.ToList(),
            Confidence = finding.Confidence
        };

    private static void MergeFinding(ResearchFinding existing, ResearchFinding incoming)
    {
        if (incoming.Confidence > existing.Confidence)
        {
            existing.Confidence = (existing.Confidence + incoming.Confidence) / 2;
        }
        else
        {
            existing.Confidence = Math.Min(1d, (existing.Confidence * 0.6d) + (incoming.Confidence * 0.4d));
        }

        if (!string.Equals(existing.Content, incoming.Content, StringComparison.OrdinalIgnoreCase))
        {
            existing.Content = string.Join(
                "\n\n",
                new[] { existing.Content, incoming.Content }
                    .Where(chunk => !string.IsNullOrWhiteSpace(chunk))
                    .Distinct());
        }

        var citationMap = existing.Citations.ToDictionary(c => c.SourceId, StringComparer.Ordinal);
        foreach (var citation in incoming.Citations)
        {
            if (!citationMap.ContainsKey(citation.SourceId))
            {
                existing.Citations.Add(citation);
            }
        }
    }
}



