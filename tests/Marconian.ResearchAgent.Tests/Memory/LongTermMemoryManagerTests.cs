using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Services.Cosmos;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Moq;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Memory;

[TestFixture]
public sealed class LongTermMemoryManagerTests
{
    [Test]
    public async Task StoreFindingAsync_PersistsRecordWithEmbedding()
    {
        var cosmosMock = new Mock<ICosmosMemoryService>();
        var openAiMock = new Mock<IAzureOpenAiService>();
        var embedding = new List<float> { 0.1f, 0.2f, 0.3f };

    cosmosMock.SetupGet(service => service.VectorDimensions).Returns(embedding.Count);

        openAiMock
            .Setup(service => service.GenerateEmbeddingAsync(It.IsAny<OpenAiEmbeddingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        MemoryRecord? capturedRecord = null;
        cosmosMock
            .Setup(service => service.UpsertRecordAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
            .Callback<MemoryRecord, CancellationToken>((record, _) => capturedRecord = record)
            .Returns(Task.CompletedTask);

        var manager = new LongTermMemoryManager(cosmosMock.Object, openAiMock.Object, "embedding");

        var finding = new ResearchFinding
        {
            Title = "Synthetic Finding",
            Content = "Important research content",
            Citations = new List<SourceCitation>
            {
                new("source-1", "Source Title", "https://example.com", "snippet")
            },
            Confidence = 0.85d
        };

        await manager.StoreFindingAsync("session-123", finding, cancellationToken: CancellationToken.None);

        Assert.That(capturedRecord, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(capturedRecord!.Content, Is.EqualTo(finding.Content));
            Assert.That(capturedRecord.Type, Is.EqualTo("research_finding"));
            Assert.That(capturedRecord.Embedding, Is.EqualTo(embedding));
            Assert.That(capturedRecord.Sources, Has.Count.EqualTo(1));
            Assert.That(capturedRecord.Metadata["confidence"], Is.EqualTo(finding.Confidence.ToString("F2")));
        });

        cosmosMock.Verify(service => service.UpsertRecordAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SearchRelevantAsync_DelegatesToCosmosSimilarity()
    {
    var cosmosMock = new Mock<ICosmosMemoryService>();
        var openAiMock = new Mock<IAzureOpenAiService>();
        var embedding = new List<float> { 0.42f, 0.99f };

    cosmosMock.SetupGet(service => service.VectorDimensions).Returns(embedding.Count);

        openAiMock
            .Setup(service => service.GenerateEmbeddingAsync(It.IsAny<OpenAiEmbeddingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        var expected = new List<MemorySearchResult>
        {
            new(
                new MemoryRecord
                {
                    ResearchSessionId = "session-123",
                    Type = "memo",
                    Content = "stored content"
                },
                0.87d)
        };

        cosmosMock
            .Setup(service => service.QuerySimilarAsync("session-123", embedding, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var manager = new LongTermMemoryManager(cosmosMock.Object, openAiMock.Object, "embedding");

        var results = await manager.SearchRelevantAsync("session-123", "query text", 5, CancellationToken.None);

        Assert.That(results, Is.SameAs(expected));
        openAiMock.Verify(service => service.GenerateEmbeddingAsync(It.IsAny<OpenAiEmbeddingRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        cosmosMock.Verify(service => service.QuerySimilarAsync("session-123", embedding, 5, It.IsAny<CancellationToken>()), Times.Once);
    }
}
