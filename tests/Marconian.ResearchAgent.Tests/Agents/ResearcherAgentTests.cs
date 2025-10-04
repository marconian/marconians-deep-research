using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Marconian.ResearchAgent.Agents;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Files;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Services.ComputerUse;
using Marconian.ResearchAgent.Services.Cosmos;
using Marconian.ResearchAgent.Services.Files;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Marconian.ResearchAgent.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Marconian.ResearchAgent.Models.Tools;

namespace Marconian.ResearchAgent.Tests.Agents;

[TestFixture]
public sealed class ResearcherAgentTests
{
    [Test]
    public async Task ExecuteTaskAsync_ShouldInvokeComputerUseNavigatorForSelectedResults()
    {
        var textResponses = new Queue<string>(new[]
        {
            "{\"selected\":[2],\"notes\":\"Official documentation\"}",
            "Summary outcome with high confidence"
        });

        var openAiMock = new Mock<IAzureOpenAiService>();
        openAiMock
            .Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => textResponses.Count > 0 ? textResponses.Dequeue() : "Fallback response");
        openAiMock
            .Setup(service => service.GenerateEmbeddingAsync(It.IsAny<OpenAiEmbeddingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float> { 0.1f, 0.2f, 0.3f });

        var cosmosMock = CreateCosmosMock();
        cosmosMock
            .Setup(service => service.UpsertRecordAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        cosmosMock
            .Setup(service => service.QuerySimilarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<float>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MemorySearchResult>());

        var memoryManager = new LongTermMemoryManager(cosmosMock.Object, openAiMock.Object, "embedding", NullLogger<LongTermMemoryManager>.Instance);

        var explorer = new StubComputerUseExplorer
        {
            Result = new ComputerUseExplorationResult(
                "https://example.com/2",
                "https://example.com/2",
                "Result Two",
                "Key findings from result two",
                "{\"summary\":\"Key findings from result two\",\"findings\":[\"Scrolled page\",\"Captured section\"],\"flagged\":[]}",
                new[] { "Scrolled page", "Captured section" },
                new[] { "Visited https://example.com/2" },
                Array.Empty<FlaggedResource>())
        };

        using var httpClient = new HttpClient(new FakeGoogleHandler())
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        using var webSearchTool = new WebSearchTool(
            apiKey: "test",
            searchEngineId: "engine",
            cacheService: null,
            httpClient: httpClient,
            logger: NullLogger<WebSearchTool>.Instance,
            provider: WebSearchProvider.GoogleApi);

        var navigatorTool = new ComputerUseNavigatorTool(explorer, NullLogger<ComputerUseNavigatorTool>.Instance);

        var tools = new ITool[] { webSearchTool, navigatorTool };

        var researcher = new ResearcherAgent(
            openAiMock.Object,
            memoryManager,
            tools,
            "chat",
            cacheService: null,
            flowTracker: null,
            logger: NullLogger<ResearcherAgent>.Instance,
            shortTermLogger: NullLogger<ShortTermMemoryManager>.Instance);

        var task = new AgentTask
        {
            TaskId = "task",
            ResearchSessionId = "session",
            Objective = "Evaluate frameworks for building resilient distributed systems",
            Parameters = new Dictionary<string, string>(),
            ContextHints = new List<string>()
        };

        AgentExecutionResult result = await researcher.ExecuteTaskAsync(task, CancellationToken.None).ConfigureAwait(false);

        Assert.That(result.Success, Is.True);
        Assert.That(explorer.RequestedUrls, Is.EquivalentTo(new[] { "https://example.com/2" }));
        Assert.That(result.ToolOutputs.Any(output =>
            output.ToolName == "ComputerUseNavigator" &&
            output.Metadata is not null &&
            output.Metadata.TryGetValue("requestedUrl", out var url) &&
            url == "https://example.com/2"), Is.True);

        cosmosMock.Verify(service => service.UpsertRecordAsync(
                It.Is<MemoryRecord>(record => string.Equals(record.Type, "search_selection", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Test]
    public async Task ExecuteTaskAsync_ShouldDownloadFlaggedPdfWithDocumentIntelligence()
    {
        var textResponses = new Queue<string>(new[]
        {
            "{\"selected\":[1],\"notes\":\"Capture supporting materials\"}",
            "Synthesized conclusion"
        });

        var openAiMock = new Mock<IAzureOpenAiService>();
        openAiMock
            .Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => textResponses.Count > 0 ? textResponses.Dequeue() : "Fallback response");
        openAiMock
            .Setup(service => service.GenerateEmbeddingAsync(It.IsAny<OpenAiEmbeddingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float> { 0.2f, 0.4f, 0.6f });

        var cosmosMock = CreateCosmosMock();
        cosmosMock
            .Setup(service => service.UpsertRecordAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        cosmosMock
            .Setup(service => service.QuerySimilarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<float>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MemorySearchResult>());

        var memoryManager = new LongTermMemoryManager(cosmosMock.Object, openAiMock.Object, "embedding", NullLogger<LongTermMemoryManager>.Instance);

        const string flaggedPdfUrl = "https://flagged.example/report.pdf";

        var explorer = new StubComputerUseExplorer
        {
            Result = new ComputerUseExplorationResult(
                "https://example.com/1",
                "https://example.com/1",
                "Result One",
                "Key findings",
                "{\"summary\":\"Key findings\",\"findings\":[\"Scrolled\",\"Captured section\"],\"flagged\":[]}",
                new[] { "Scrolled", "Captured section" },
                new[] { "Visited https://example.com/1" },
                new[]
                {
                    new FlaggedResource(FlaggedResourceType.Page, "Regulatory report", flaggedPdfUrl, null, "Computer-use suggested download")
                })
        };

        using var searchClient = new HttpClient(new FakeGoogleHandler())
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        using var webSearchTool = new WebSearchTool(
            apiKey: "test",
            searchEngineId: "engine",
            cacheService: null,
            httpClient: searchClient,
            logger: NullLogger<WebSearchTool>.Instance,
            provider: WebSearchProvider.GoogleApi);

        var recordingHandler = new RecordingHttpHandler();
        using var scraperHttpClient = new HttpClient(recordingHandler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        await using var scraperTool = new WebScraperTool(
            cacheService: null,
            httpClient: scraperHttpClient,
            logger: NullLogger<WebScraperTool>.Instance,
            fileRegistryService: null);

        var registryMock = new Mock<IFileRegistryService>();
        var capturedSources = new List<string?>();
        registryMock
            .Setup(service => service.SaveFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sessionId, string fileName, Stream _, string? contentType, string? sourceUrl, CancellationToken _) =>
            {
                capturedSources.Add(sourceUrl);
                return new FileRegistryEntry
                {
                    FileId = Guid.NewGuid().ToString("N"),
                    FileName = fileName,
                    RelativePath = Path.Combine(sessionId, fileName),
                    SourceUrl = sourceUrl,
                    ContentType = contentType
                };
            });

        int analyzeCallCount = 0;
        var documentIntelligenceMock = new Mock<IDocumentIntelligenceService>();
        documentIntelligenceMock
            .Setup(service => service.AnalyzeDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => analyzeCallCount++)
            .ReturnsAsync(new DocumentAnalysisResult { Text = "Extracted PDF text" });

        var pdfHandler = new PdfHttpHandler();
        using var fileReaderTool = new FileReaderTool(
            registryMock.Object,
            documentIntelligenceMock.Object,
            httpClient: new HttpClient(pdfHandler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            },
            logger: NullLogger<FileReaderTool>.Instance);

        var navigatorTool = new ComputerUseNavigatorTool(explorer, NullLogger<ComputerUseNavigatorTool>.Instance);

        var tools = new ITool[] { webSearchTool, navigatorTool, scraperTool, fileReaderTool };

        var researcher = new ResearcherAgent(
            openAiMock.Object,
            memoryManager,
            tools,
            "chat",
            cacheService: null,
            flowTracker: null,
            logger: NullLogger<ResearcherAgent>.Instance,
            shortTermLogger: NullLogger<ShortTermMemoryManager>.Instance);

        var task = new AgentTask
        {
            TaskId = "task",
            ResearchSessionId = "session",
            Objective = "Assess research artefacts",
            Parameters = new Dictionary<string, string>(),
            ContextHints = new List<string>()
        };

        AgentExecutionResult result = await researcher.ExecuteTaskAsync(task, CancellationToken.None).ConfigureAwait(false);

        Assert.That(result.Success, Is.True);
        Assert.That(analyzeCallCount, Is.EqualTo(1), "PDFs should be routed through Document Intelligence");
        Assert.That(capturedSources, Does.Contain(flaggedPdfUrl));
        Assert.That(pdfHandler.RequestedUris.Select(uri => uri.ToString()), Does.Contain(flaggedPdfUrl));
        Assert.That(recordingHandler.RequestedUris.Select(uri => uri.ToString()), Does.Not.Contain(flaggedPdfUrl));

        registryMock.Verify(service => service.SaveFileAsync(
                task.ResearchSessionId,
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<string?>(),
                flaggedPdfUrl,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Mock<ICosmosMemoryService> CreateCosmosMock()
    {
        var mock = new Mock<ICosmosMemoryService>();
        mock.Setup(service => service.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mock.SetupGet(service => service.VectorDimensions).Returns(1536);
        mock.Setup(service => service.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return mock;
    }

    [Test]
    public async Task ExecuteTaskAsync_ShouldScrapeFlaggedPages()
    {
        var textResponses = new Queue<string>(new[]
        {
            "{\"selected\":[1],\"notes\":\"Flag the secondary resource\"}",
            "Summarized findings"
        });

        var openAiMock = new Mock<IAzureOpenAiService>();
        openAiMock
            .Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => textResponses.Count > 0 ? textResponses.Dequeue() : "Fallback response");
        openAiMock
            .Setup(service => service.GenerateEmbeddingAsync(It.IsAny<OpenAiEmbeddingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float> { 0.05f, 0.15f, 0.25f });

        var cosmosMock = CreateCosmosMock();
        cosmosMock
            .Setup(service => service.UpsertRecordAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        cosmosMock
            .Setup(service => service.QuerySimilarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<float>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MemorySearchResult>());

        var memoryManager = new LongTermMemoryManager(cosmosMock.Object, openAiMock.Object, "embedding", NullLogger<LongTermMemoryManager>.Instance);

        var explorer = new StubComputerUseExplorer
        {
            Result = new ComputerUseExplorationResult(
                "https://example.com/1",
                "https://example.com/1",
                "Result One",
                "Key findings",
                "{\"summary\":\"Key findings\",\"findings\":[\"Scrolled\",\"Captured section\"],\"flagged\":[]}",
                new[] { "Scrolled", "Captured section" },
                new[] { "Visited https://example.com/1" },
                new[]
                {
                    new FlaggedResource(FlaggedResourceType.Page, "Supporting dataset", "https://flagged.example/dataset", null, "Contains statistics table")
                })
        };

        using var searchClient = new HttpClient(new FakeGoogleHandler())
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        using var webSearchTool = new WebSearchTool(
            apiKey: "test",
            searchEngineId: "engine",
            cacheService: null,
            httpClient: searchClient,
            logger: NullLogger<WebSearchTool>.Instance,
            provider: WebSearchProvider.GoogleApi);

        var recordingHandler = new RecordingHttpHandler();
        using var scraperHttpClient = new HttpClient(recordingHandler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        await using var scraperTool = new WebScraperTool(
            cacheService: null,
            httpClient: scraperHttpClient,
            logger: NullLogger<WebScraperTool>.Instance,
            fileRegistryService: null);

        var navigatorTool = new ComputerUseNavigatorTool(explorer, NullLogger<ComputerUseNavigatorTool>.Instance);

    var tools = new ITool[] { webSearchTool, navigatorTool, scraperTool };

        var researcher = new ResearcherAgent(
            openAiMock.Object,
            memoryManager,
            tools,
            "chat",
            cacheService: null,
            flowTracker: null,
            logger: NullLogger<ResearcherAgent>.Instance,
            shortTermLogger: NullLogger<ShortTermMemoryManager>.Instance);

        var task = new AgentTask
        {
            TaskId = "task",
            ResearchSessionId = "session",
            Objective = "Assess research artefacts",
            Parameters = new Dictionary<string, string>(),
            ContextHints = new List<string>()
        };

        AgentExecutionResult result = await researcher.ExecuteTaskAsync(task, CancellationToken.None).ConfigureAwait(false);

        Assert.That(result.Success, Is.True);
        Assert.That(recordingHandler.RequestedUris.Select(uri => uri.ToString()), Does.Contain("https://flagged.example/dataset"));
    }

    private sealed class StubComputerUseExplorer : IComputerUseExplorer
    {
        public List<string> RequestedUrls { get; } = new();

        public ComputerUseExplorationResult Result { get; set; } = new(
            "https://example.com",
            "https://example.com",
            "Example",
            "Summary",
            "{\"summary\":\"Summary\",\"findings\":[],\"flagged\":[]}",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<FlaggedResource>());

        public Task<ComputerUseExplorationResult> ExploreAsync(string url, string? objective, CancellationToken cancellationToken = default)
        {
            RequestedUrls.Add(url);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeGoogleHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string payload = "{\"items\":[{\"title\":\"Result One\",\"link\":\"https://example.com/1\",\"snippet\":\"Snippet 1\"},{\"title\":\"Result Two\",\"link\":\"https://example.com/2\",\"snippet\":\"Snippet 2\"},{\"title\":\"Result Three\",\"link\":\"https://example.com/3\",\"snippet\":\"Snippet 3\"}],\"searchInformation\":{\"totalResults\":\"3\"}}";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }

    private sealed class PdfHttpHandler : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null)
            {
                throw new InvalidOperationException("Request URI cannot be null.");
            }

            RequestedUris.Add(request.RequestUri);

            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 simulated content"));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = "report.pdf"
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };

            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                RequestedUris.Add(request.RequestUri);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><p>content</p></body></html>", Encoding.UTF8, "text/html")
            };

            return Task.FromResult(response);
        }
    }
}
