using System;

namespace Marconian.ResearchAgent.Streaming;

public sealed class ConsoleStreamingOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan SummaryInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 5;

    public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool UseSummarizer { get; set; } = true;

    public string? SummarizerModel { get; set; }

    public bool UseLlmSummarizer { get; set; } = false;

    public TimeSpan SummarizerMinInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int SummarizerMaxTokens { get; set; } = 200;
}
