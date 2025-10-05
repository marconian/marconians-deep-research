using System;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Synthesis;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Synthesis;

[TestFixture]
public sealed class MarkdownReportBuilderTests
{
    [Test]
    public void Build_ShouldNormalizeCitationWhitespace()
    {
        var builder = new MarkdownReportBuilder();
        var outline = new ReportOutline
        {
            Objective = "Understand migratory report formatting"
        };

        var draft = new ReportSectionDraft
        {
            SectionId = "sec-1",
            Title = "Section",
            Content = "Body without inline citations.",
        };

        draft.Citations.Add(new SourceCitation(
            "source-1",
            "Example\nTitle",
            "https://example.com/resource",
            "First line of abstract.\nSecond line\twith extra   spaces."));

        string report = builder.Build(
            rootQuestion: "What is happening?",
            executiveSummary: "Summary",
            outline,
            new[] { draft },
            Array.Empty<ResearchFinding>());

        string expectedCitation = "[^1]: Example Title – https://example.com/resource – First line of abstract. Second line with extra spaces.";
        Assert.That(report, Does.Contain(expectedCitation));
    }

    [Test]
    public void BuildSourcesSection_ShouldReturnFormattedSourcesBlock()
    {
        var builder = new MarkdownReportBuilder();
        var citations = new[]
        {
            new SourceCitation(
                "source-1",
                " Primary Title \n",
                "https://example.com/one",
                "Snippet one\nwith \textra spacing"),
            new SourceCitation(
                "source-1",
                "Duplicate Title",
                "https://example.com/duplicate",
                "Duplicate snippet"),
            new SourceCitation(
                "source-2",
                null,
                null,
                "Second snippet")
        };

        string result = builder.BuildSourcesSection(citations);

        string[] expectedLines =
        {
            "## Sources",
            string.Empty,
            "[^1]: Primary Title – https://example.com/one – Snippet one with extra spacing",
            "[^2]: source-2 – Second snippet"
        };
        string expected = string.Join(Environment.NewLine, expectedLines);
        Assert.That(result, Is.EqualTo(expected));
    }
}
