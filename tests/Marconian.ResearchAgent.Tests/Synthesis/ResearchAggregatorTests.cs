using System.Collections.Generic;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Research;
using Marconian.ResearchAgent.Synthesis;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Synthesis;

[TestFixture]
public sealed class ResearchAggregatorTests
{
    [Test]
    public void Aggregate_ShouldDeduplicateCitationsAcrossFindings()
    {
        var aggregator = new ResearchAggregator();

        var branchResults = new[]
        {
            new ResearchBranchResult
            {
                BranchId = "branch-1",
                Finding = new ResearchFinding
                {
                    Title = "Climate impacts",
                    Content = "Initial finding",
                    Citations = new List<SourceCitation>
                    {
                        new("scrape:abc", "Conference Abstract", "https://example.org/docs/report.pdf", "Short summary")
                    },
                    Confidence = 0.9d
                }
            },
            new ResearchBranchResult
            {
                BranchId = "branch-2",
                Finding = new ResearchFinding
                {
                    Title = "Habitat notes",
                    Content = "Follow-up finding",
                    Citations = new List<SourceCitation>
                    {
                        new("gcs:1", "Conference Abstract", "https://example.org/docs/report.pdf", "Extended summary with additional context")
                    },
                    Confidence = 0.7d
                }
            }
        };

        ResearchAggregationResult result = aggregator.Aggregate(branchResults);

        Assert.That(result.UniqueCitations, Has.Count.EqualTo(1));
        SourceCitation citation = result.UniqueCitations[0];
        Assert.That(citation.Url, Is.EqualTo("https://example.org/docs/report.pdf"));
        Assert.That(citation.Snippet, Is.EqualTo("Extended summary with additional context"));
    }
}
