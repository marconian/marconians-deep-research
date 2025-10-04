using System.Linq;
using Marconian.ResearchAgent.Agents;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Agents;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Research;
using Marconian.ResearchAgent.Services.Cosmos;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Marconian.ResearchAgent.Tools;
using Moq;
using NUnit.Framework;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Tests.Agents;

[TestFixture]
public sealed class OrchestratorAgentTests
{
    [Test]
    public async Task ExecuteTaskAsync_WhenPlanningFails_SetsFailedState()
    {
        var openAiMock = new Mock<IAzureOpenAiService>();
        openAiMock
            .Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("planner failed"));
        openAiMock
            .Setup(service => service.GenerateEmbeddingAsync(It.IsAny<OpenAiEmbeddingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float> { 0.1f, 0.2f, 0.3f });

        var cosmosMock = CreateCosmosMock();
        var memoryManager = new LongTermMemoryManager(cosmosMock.Object, openAiMock.Object, "embedding", NullLogger<LongTermMemoryManager>.Instance);

        string reportsDir = CreateTempDirectory();
        try
        {
            var orchestrator = new OrchestratorAgent(
                openAiMock.Object,
                memoryManager,
                () => Array.Empty<ITool>(),
                "chat",
                NullLogger<OrchestratorAgent>.Instance,
                NullLoggerFactory.Instance,
                cacheService: null,
                reportsDirectory: reportsDir);

            var task = new AgentTask
            {
                TaskId = "task-1",
                ResearchSessionId = "session-1",
                Objective = "Test objective",
                Parameters = new Dictionary<string, string>(),
                ContextHints = new List<string>()
            };

            AgentExecutionResult result = await orchestrator.ExecuteTaskAsync(task, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(orchestrator.CurrentState, Is.EqualTo(OrchestratorState.Failed));
        }
        finally
        {
            Directory.Delete(reportsDir, true);
        }
    }

    [Test]
    public async Task ExecuteTaskAsync_CompletesSuccessfullyWithMinimalDependencies()
    {
        var responseQueue = new Queue<string>(new[]
        {
            "1. Investigate topic",
            "Answer summary with high confidence",
            "{\"continue\": false, \"followUpQuestions\": []}",
            "Synthesis summary",
            "{\"notes\": \"\", \"coreSections\": [{\"title\": \"Key Findings\", \"summary\": \"Summarize the key insight\", \"supportingFindingIds\": []}], \"generalSections\": []}",
            "Detailed section content grounded in evidence."
        });

        var openAiMock = new Mock<IAzureOpenAiService>();
        openAiMock
            .Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => responseQueue.Count > 0 ? responseQueue.Dequeue() : "Fallback response");
        openAiMock
            .Setup(service => service.GenerateEmbeddingAsync(It.IsAny<OpenAiEmbeddingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float> { 0.1f, 0.2f, 0.3f });

        var cosmosMock = CreateCosmosMock();
        var storedRecords = new List<MemoryRecord>();
        cosmosMock
            .Setup(service => service.UpsertRecordAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
            .Callback<MemoryRecord, CancellationToken>((record, _) => storedRecords.Add(record))
            .Returns(Task.CompletedTask);
        cosmosMock
            .Setup(service => service.QuerySimilarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<float>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MemorySearchResult>());

        var memoryManager = new LongTermMemoryManager(cosmosMock.Object, openAiMock.Object, "embedding", NullLogger<LongTermMemoryManager>.Instance);

        string reportsDir = CreateTempDirectory();
        try
        {
            var orchestrator = new OrchestratorAgent(
                openAiMock.Object,
                memoryManager,
                () => Array.Empty<ITool>(),
                "chat",
                NullLogger<OrchestratorAgent>.Instance,
                NullLoggerFactory.Instance,
                cacheService: null,
                reportsDirectory: reportsDir,
                orchestratorOptions: new OrchestratorOptions { MaxReportRevisionPasses = 0 });

            var task = new AgentTask
            {
                TaskId = "task-1",
                ResearchSessionId = "session-1",
                Objective = "Investigate the effect of X",
                Parameters = new Dictionary<string, string>(),
                ContextHints = new List<string>()
            };

            AgentExecutionResult result = await orchestrator.ExecuteTaskAsync(task, CancellationToken.None);

            Assert.That(result.Success, Is.True, () =>
            {
                string errorSummary = result.Errors.Count == 0
                    ? "No errors captured."
                    : string.Join(" | ", result.Errors);
                return $"Execution reported failure. Errors: {errorSummary}";
            });
            Assert.That(orchestrator.CurrentState, Is.EqualTo(OrchestratorState.Completed));
            Assert.That(result.Summary, Is.EqualTo("Synthesis summary"));
            Assert.That(result.Metadata.TryGetValue("reportPath", out var reportPath), Is.True);
            Assert.That(reportPath, Is.Not.Null.And.Not.Empty);
            Assert.That(File.Exists(reportPath!), Is.True);
            Assert.That(storedRecords, Has.Count.GreaterThanOrEqualTo(2));

            var outlineRequest = openAiMock.Invocations
                .Where(invocation => invocation.Method.Name == nameof(IAzureOpenAiService.GenerateTextAsync))
                .Select(invocation => invocation.Arguments.FirstOrDefault() as OpenAiChatRequest)
                .FirstOrDefault(request => request is not null && request.SystemPrompt == SystemPrompts.Orchestrator.OutlineEditor);

            Assert.That(outlineRequest, Is.Not.Null, "Outline request should have been issued.");
            Assert.That(outlineRequest!.JsonSchemaFormat, Is.Not.Null, "Outline request must enforce the structured JSON schema.");
        }
        finally
        {
            Directory.Delete(reportsDir, true);
        }
    }

    private static Mock<ICosmosMemoryService> CreateCosmosMock()
    {
        var mock = new Mock<ICosmosMemoryService>();
        mock.Setup(service => service.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mock.SetupGet(service => service.VectorDimensions).Returns(3072);
        mock.Setup(service => service.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return mock;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "MarconianTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
