using System.Diagnostics.CodeAnalysis;

namespace Marconian.ResearchAgent.Streaming;

public enum ThoughtSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ThoughtEvent(
    string Phase,
    string Actor,
    string Detail,
    DateTimeOffset Timestamp,
    Guid? CorrelationId = null,
    ThoughtSeverity Severity = ThoughtSeverity.Info)
{
    [MemberNotNullWhen(true, nameof(CorrelationId))]
    public bool HasCorrelation => CorrelationId.HasValue;
}
