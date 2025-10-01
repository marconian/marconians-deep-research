using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Tools;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Integration;

[TestFixture]
public sealed class WebSearchToolIntegrationTests
{
    [Test]
    public async Task ExecuteAsync_ReturnsResultsWhenCredentialsPresent()
    {
        string? apiKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        string? searchEngineId = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_ENGINE_ID");

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(searchEngineId))
        {
            Assert.Ignore("Google search credentials are not configured for integration testing.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var tool = new WebSearchTool(apiKey, searchEngineId, cacheService: null, httpClient: httpClient);

        var context = new ToolExecutionContext
        {
            ResearchSessionId = "integration-session",
            AgentId = "integration-agent",
            Instruction = "Latest developments in quantum computing",
            Parameters = new Dictionary<string, string>
            {
                ["query"] = "Latest developments in quantum computing"
            }
        };

        ToolExecutionResult result = await tool.ExecuteAsync(context, CancellationToken.None);

        Assert.That(result.Success, Is.True, "Web search should succeed when credentials are available.");
        Assert.That(result.Citations, Is.Not.Null);
        Assert.That(result.Citations.Count, Is.GreaterThan(0));
        Assert.That(string.IsNullOrWhiteSpace(result.Output), Is.False);
    }
}