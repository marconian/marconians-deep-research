using System;
using System.Collections.Generic;

namespace Marconian.ResearchAgent.Configuration;

public sealed record ComputerUseOptions
{
    public bool Headless { get; init; } = false;

    public bool UsePersistentContext { get; init; } = true;

    public int MaxConcurrentSessions { get; init; } = 1;

    public string? UserDataDirectory { get; init; }
        = null;

    public string? PreferredLocale { get; init; }
        = null;

    public string? PreferredTimeZoneId { get; init; }
        = null;

    public ComputerUseViewportOptions Viewport { get; init; }
        = new();

    public string? UserAgent { get; init; }
        = null;

    public ComputerUseStealthOptions Stealth { get; init; }
        = new();

    public ComputerUseProxyOptions Proxy { get; init; }
        = new();

    public ComputerUseBehaviorOptions Behavior { get; init; }
        = new();

    public ComputerUseDriverOptions Driver { get; init; }
        = new();
}

public sealed record ComputerUseViewportOptions
{
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    public int DeviceScaleFactor { get; init; } = 1;
}

public sealed record ComputerUseStealthOptions
{
    public ComputerUseStealthProvider Provider { get; init; } = ComputerUseStealthProvider.Native;

    public bool EnableScriptPatches { get; init; } = true;

    public bool EnableConsoleQuiets { get; init; } = true;

    public bool LogDiagnostics { get; init; } = true;
}

public enum ComputerUseStealthProvider
{
    None,
    Native,
    Soenneker,
    Undetected
}

public sealed record ComputerUseProxyOptions
{
    public bool Enabled { get; init; }
        = false;

    public bool RotatePerSession { get; init; }
        = true;

    public IReadOnlyList<ComputerUseProxyEndpoint> Endpoints { get; init; }
        = Array.Empty<ComputerUseProxyEndpoint>();
}

public sealed record ComputerUseProxyEndpoint
{
    public required string Uri { get; init; }
        = string.Empty;

    public string? Username { get; init; }
        = null;

    public string? Password { get; init; }
        = null;

    public string? PreferredLocale { get; init; }
        = null;

    public string? PreferredTimeZoneId { get; init; }
        = null;

    public double? Latitude { get; init; }
        = null;

    public double? Longitude { get; init; }
        = null;

    public string? Notes { get; init; }
        = null;
}

public sealed record ComputerUseBehaviorOptions
{
    public bool EnableMouseSimulation { get; init; } = true;

    public bool EnableTypingSimulation { get; init; } = true;

    public int MaxMouseStepPixels { get; init; } = 45;

    public int MinMouseDelayMs { get; init; } = 8;

    public int MaxMouseDelayMs { get; init; } = 24;

    public int MinClickHoldMs { get; init; } = 90;

    public int MaxClickHoldMs { get; init; } = 180;

    public int MinTypingDelayMs { get; init; } = 45;

    public int MaxTypingDelayMs { get; init; } = 260;

    public int TypingBurstCharacters { get; init; } = 5;

    public double TypingBurstPauseMultiplier { get; init; } = 2.4;
}

public sealed record ComputerUseDriverOptions
{
    public string? DriverVariant { get; init; }
        = null;

    public string? PatchedDriverPath { get; init; }
        = null;

    public bool ValidateRuntimeEnablePatch { get; init; } = true;
}
