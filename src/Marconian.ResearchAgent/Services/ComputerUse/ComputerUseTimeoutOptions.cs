using System;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public sealed record ComputerUseTimeoutOptions
{
    /// <summary>
    /// Default timeout applied to Playwright interactions (e.g., selectors, clicks, waits).
    /// </summary>
    public TimeSpan DefaultActionTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Default timeout applied to Playwright navigation operations.
    /// </summary>
    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Maximum duration allowed for a computer-use search before timing out.
    /// </summary>
    public TimeSpan SearchOperationTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum duration allowed for a computer-use exploration session before timing out.
    /// </summary>
    public TimeSpan ExplorationOperationTimeout { get; init; } = TimeSpan.FromMinutes(4);

    public static ComputerUseTimeoutOptions Default { get; } = new();
}
