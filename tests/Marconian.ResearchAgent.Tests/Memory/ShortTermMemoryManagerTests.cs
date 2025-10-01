using Marconian.ResearchAgent.Memory;
using Marconian.ResearchAgent.Models.Memory;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Moq;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Memory;

[TestFixture]
public sealed class ShortTermMemoryManagerTests
{
    [Test]
    public async Task AppendAsync_WhenExceedingLimit_SummarizesOldestEntries()
    {
        var openAiMock = new Mock<IAzureOpenAiService>();
        openAiMock
            .Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("summary payload");

        var cacheMock = new Mock<ICacheService>();
        cacheMock
            .Setup(service => service.SetAsync(It.IsAny<string>(), It.IsAny<List<ShortTermMemoryEntry>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new ShortTermMemoryManager(
            agentId: "agent",
            researchSessionId: "session",
            openAiService: openAiMock.Object,
            deploymentName: "chat",
            cacheService: cacheMock.Object,
            maxEntries: 4,
            summaryBatchSize: 2);

        for (int i = 0; i < 5; i++)
        {
            await manager.AppendAsync("user", $"message {i}");
        }

        Assert.That(manager.Entries.Count, Is.EqualTo(3));
        Assert.That(manager.Entries[0].IsSummary, Is.True);
        Assert.That(manager.Entries[0].Content, Is.EqualTo("summary payload"));
        openAiMock.Verify(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        cacheMock.Verify(service => service.SetAsync(It.IsAny<string>(), It.IsAny<List<ShortTermMemoryEntry>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}

