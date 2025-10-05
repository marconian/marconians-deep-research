using System.Collections.Generic;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Utilities;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Utilities;

[TestFixture]
public sealed class SourceCitationDeduplicatorTests
{
    [Test]
    public void Deduplicate_ShouldNormalizeUrls()
    {
        var citations = new List<SourceCitation>
        {
            new("scrape:1", "Example Document", "HTTPS://Example.com/Path/Resource.PDF", "Snippet A"),
            new("scrape:2", "Example Document", "https://example.com/path/resource.pdf", "Snippet B with more detail")
        };

        var deduplicated = SourceCitationDeduplicator.Deduplicate(citations);

        Assert.That(deduplicated, Has.Count.EqualTo(1));
        Assert.That(deduplicated[0].Url, Is.EqualTo("https://example.com/path/resource.pdf"));
        Assert.That(deduplicated[0].Snippet, Is.EqualTo("Snippet B with more detail"));
    }

    [Test]
    public void Deduplicate_ShouldFallBackToTitleWhenUrlMissing()
    {
        var citations = new List<SourceCitation>
        {
            new("", "Shared Title", null, "First"),
            new("", "Shared Title", null, "Second snippet offering extra"),
        };

        var deduplicated = SourceCitationDeduplicator.Deduplicate(citations);

        Assert.That(deduplicated, Has.Count.EqualTo(1));
        Assert.That(deduplicated[0].Title, Is.EqualTo("Shared Title"));
        Assert.That(deduplicated[0].Snippet, Is.EqualTo("Second snippet offering extra"));
    }
}
