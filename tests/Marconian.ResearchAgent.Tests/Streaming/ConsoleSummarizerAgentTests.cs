using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Marconian.ResearchAgent.Streaming;
using Microsoft.Extensions.Logging;
using Moq;

namespace Marconian.ResearchAgent.Tests.Streaming;

[TestFixture]
public sealed class ConsoleSummarizerAgentTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task SummarizeAsync_ReturnsNull_WhenNoEvents()
    {
        var openAi = new Mock<IAzureOpenAiService>(MockBehavior.Strict);
        var options = new ConsoleStreamingOptions
        {
            UseSummarizer = true,
            UseLlmSummarizer = true
        };
        var summarizer = CreateSummarizer(openAi.Object, options);

        string? result = await summarizer.SummarizeAsync(Array.Empty<ThoughtEvent>(), CancellationToken.None);

        Assert.That(result, Is.Null);
        openAi.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SummarizeAsync_UsesFallback_WhenLlmDisabled()
    {
        var openAi = new Mock<IAzureOpenAiService>(MockBehavior.Strict);
        var options = new ConsoleStreamingOptions
        {
            UseSummarizer = true,
            UseLlmSummarizer = false
        };
        var summarizer = CreateSummarizer(openAi.Object, options);

        string? result = await summarizer.SummarizeAsync(CreateEvents("Fallback detail"), CancellationToken.None);

        Assert.That(result, Is.Not.Null.And.Contains("Fallback detail"));
        openAi.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SummarizeAsync_UsesOpenAi_WhenEnabled()
    {
        var openAi = new Mock<IAzureOpenAiService>();
        OpenAiChatRequest? captured = null;
        openAi.Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OpenAiChatRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync("  Summarized output  ");

        var options = new ConsoleStreamingOptions
        {
            UseSummarizer = true,
            UseLlmSummarizer = true,
            SummarizerMaxTokens = 150
        };
        var summarizer = CreateSummarizer(openAi.Object, options, deployment: "custom-deployment");

        string? result = await summarizer.SummarizeAsync(CreateEvents("Primary detail"), CancellationToken.None);

        Assert.That(result, Is.EqualTo("Summarized output"));
        Assert.That(captured, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(captured!.DeploymentName, Is.EqualTo("custom-deployment"));
            Assert.That(captured.MaxOutputTokens, Is.EqualTo(150));
            Assert.That(captured.Messages, Has.Count.EqualTo(1));
            Assert.That(captured.Messages[0].Content, Does.Contain("Primary detail"));
        });

        openAi.Verify(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SummarizeAsync_ThrottlesRepeatedInvocations()
    {
        var openAi = new Mock<IAzureOpenAiService>();
        openAi.Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("First summary");

        var options = new ConsoleStreamingOptions
        {
            UseSummarizer = true,
            UseLlmSummarizer = true,
            SummarizerMinInterval = TimeSpan.FromMinutes(10)
        };
        var summarizer = CreateSummarizer(openAi.Object, options);
        var events = CreateEvents("Throttled detail");

        string? first = await summarizer.SummarizeAsync(events, CancellationToken.None);
        string? second = await summarizer.SummarizeAsync(events, CancellationToken.None);

        Assert.That(first, Is.EqualTo("First summary"));
        Assert.That(second, Is.Not.Null.And.Contains("Throttled detail"));

        openAi.Verify(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SummarizeAsync_FallsBackWhenOpenAiFails()
    {
        var openAi = new Mock<IAzureOpenAiService>();
        openAi.Setup(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Service unavailable"));

        var options = new ConsoleStreamingOptions
        {
            UseSummarizer = true,
            UseLlmSummarizer = true
        };
        var summarizer = CreateSummarizer(openAi.Object, options);

        string? result = await summarizer.SummarizeAsync(CreateEvents("Fallback on error"), CancellationToken.None);

        Assert.That(result, Is.Not.Null.And.Contains("Fallback on error"));
        openAi.Verify(service => service.GenerateTextAsync(It.IsAny<OpenAiChatRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ConsoleSummarizerAgent CreateSummarizer(IAzureOpenAiService openAiService, ConsoleStreamingOptions options, string deployment = "chat-default")
    {
        var logger = Mock.Of<ILogger<ConsoleSummarizerAgent>>();
        return new ConsoleSummarizerAgent(openAiService, deployment, options, logger);
    }

    private static IReadOnlyList<ThoughtEvent> CreateEvents(string detail)
    {
        return new[]
        {
            new ThoughtEvent("phase", "actor", detail, FixedTimestamp)
        };
    }
}
