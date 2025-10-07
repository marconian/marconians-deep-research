using System;
using System.Collections.Generic;
using System.Linq;
using Marconian.ResearchAgent.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Streaming;

public sealed class ConsoleStatusRouter
{
    private readonly IThoughtEventPublisher _publisher;
    private readonly ILogger<ConsoleStatusRouter> _logger;

    public static ConsoleStatusRouter Disabled { get; } = new ConsoleStatusRouter(
        NullThoughtEventPublisher.Instance,
        streamingEnabled: false,
        NullLogger<ConsoleStatusRouter>.Instance);

    public ConsoleStatusRouter(
        IThoughtEventPublisher publisher,
        bool streamingEnabled,
        ILogger<ConsoleStatusRouter>? logger = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        StreamingEnabled = streamingEnabled;
        _logger = logger ?? NullLogger<ConsoleStatusRouter>.Instance;
    }

    public bool StreamingEnabled { get; }

    private static string DefaultPhase { get; } = "Lifecycle";
    private static string DefaultActor { get; } = "Console";

    public void Info(
        string message,
        string? phase = null,
        string? actor = null,
        bool alwaysWriteToConsole = false)
        => Publish(message, ThoughtSeverity.Info, phase, actor, alwaysWriteToConsole);

    public void Warning(
        string message,
        string? phase = null,
        string? actor = null,
        bool alwaysWriteToConsole = false)
        => Publish(message, ThoughtSeverity.Warning, phase, actor, alwaysWriteToConsole);

    public void Error(
        string message,
        string? phase = null,
        string? actor = null,
        bool alwaysWriteToConsole = true)
        => Publish(message, ThoughtSeverity.Error, phase, actor, alwaysWriteToConsole: alwaysWriteToConsole);

    public void Publish(
        string message,
        ThoughtSeverity severity,
        string? phase = null,
        string? actor = null,
        bool alwaysWriteToConsole = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string resolvedPhase = phase ?? DefaultPhase;
        string resolvedActor = actor ?? DefaultActor;

        try
        {
            _publisher.Publish(resolvedPhase, resolvedActor, message, severity);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish thought event {Phase}/{Actor}.", resolvedPhase, resolvedActor);
        }

        if (alwaysWriteToConsole || !StreamingEnabled)
        {
            WriteToConsole(message, severity);
        }
    }

    public void PublishBlock(
        IEnumerable<string> lines,
        ThoughtSeverity severity,
        string? phase = null,
        string? actor = null,
        bool alwaysWriteToConsole = false)
    {
        if (lines is null)
        {
            return;
        }

        IReadOnlyList<string> array = lines.Where(static line => line is not null).Select(static line => line!).ToArray();
        if (array.Count == 0)
        {
            return;
        }

        string joined = string.Join(Environment.NewLine, array);
        Publish(joined, severity, phase, actor, alwaysWriteToConsole);

        if (alwaysWriteToConsole || !StreamingEnabled)
        {
            foreach (string line in array)
            {
                WriteToConsole(line, severity);
            }
        }
    }

    public void PublishSuccessSummary(string summary, string? phase = null, string? actor = null)
        => Publish(summary, ThoughtSeverity.Info, phase, actor, alwaysWriteToConsole: true);

    private static void WriteToConsole(string message, ThoughtSeverity severity)
    {
        if (severity == ThoughtSeverity.Error)
        {
            Console.Error.WriteLine(message);
            return;
        }

        Console.WriteLine(message);
    }
}
