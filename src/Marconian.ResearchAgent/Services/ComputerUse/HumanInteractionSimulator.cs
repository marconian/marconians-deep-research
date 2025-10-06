using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Marconian.ResearchAgent.Configuration;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public sealed class HumanInteractionSimulator
{
    private readonly Random _random;
    private readonly ComputerUseBehaviorOptions _options;
    private readonly int _viewportWidth;
    private readonly int _viewportHeight;

    private (float X, float Y)? _lastPosition;

    public HumanInteractionSimulator(ComputerUseBehaviorOptions options, int viewportWidth, int viewportHeight, int? seed = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public async Task MoveMouseAsync(IPage page, (float X, float Y) target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!_options.EnableMouseSimulation)
        {
            await page.Mouse.MoveAsync(target.X, target.Y).ConfigureAwait(false);
            _lastPosition = target;
            return;
        }

        (float X, float Y) start = _lastPosition ?? (
            _random.Next(0, _viewportWidth),
            _random.Next(0, _viewportHeight));

        IReadOnlyList<MouseStep> path = MousePathGenerator.Generate(start, target, _options, _random, _viewportWidth, _viewportHeight);

        foreach (MouseStep step in path)
        {
            await page.Mouse.MoveAsync(step.X, step.Y).ConfigureAwait(false);
            _lastPosition = (step.X, step.Y);
            if (step.DelayMs > 0)
            {
                await Task.Delay(step.DelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task ClickAsync(IPage page, (float X, float Y) target, MouseButton button, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!_options.EnableMouseSimulation)
        {
            await page.Mouse.ClickAsync(target.X, target.Y, new MouseClickOptions { Button = button }).ConfigureAwait(false);
            _lastPosition = target;
            return;
        }

        await MoveMouseAsync(page, target, cancellationToken).ConfigureAwait(false);

        int holdDuration = _random.Next(_options.MinClickHoldMs, _options.MaxClickHoldMs + 1);
        await page.Mouse.DownAsync(new MouseDownOptions { Button = button }).ConfigureAwait(false);
        await Task.Delay(holdDuration, cancellationToken).ConfigureAwait(false);
        await page.Mouse.UpAsync(new MouseUpOptions { Button = button }).ConfigureAwait(false);
        _lastPosition = target;

        int settleDelay = _random.Next(_options.MinMouseDelayMs, _options.MaxMouseDelayMs + 1);
        await Task.Delay(settleDelay, cancellationToken).ConfigureAwait(false);
    }

    public async Task ScrollAsync(IPage page, double deltaX, double deltaY, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!_options.EnableMouseSimulation)
        {
            await page.Mouse.WheelAsync((float)deltaX, (float)deltaY).ConfigureAwait(false);
            return;
        }

        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(deltaY) / 120));
        double stepX = deltaX / steps;
        double stepY = deltaY / steps;

        for (int i = 0; i < steps; i++)
        {
            await page.Mouse.WheelAsync((float)stepX, (float)stepY).ConfigureAwait(false);
            int delay = _random.Next(_options.MinMouseDelayMs, _options.MaxMouseDelayMs + 1);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task TypeAsync(IKeyboard keyboard, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!_options.EnableTypingSimulation)
        {
            await keyboard.TypeAsync(text).ConfigureAwait(false);
            return;
        }

        int burst = 0;
        foreach (char ch in text)
        {
            await keyboard.TypeAsync(ch.ToString()).ConfigureAwait(false);
            burst++;

            int delay = _random.Next(_options.MinTypingDelayMs, _options.MaxTypingDelayMs + 1);
            if (burst >= Math.Max(1, _options.TypingBurstCharacters))
            {
                delay = (int)(delay * Math.Max(1.0, _options.TypingBurstPauseMultiplier));
                burst = 0;
            }

            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task PressKeysAsync(IKeyboard keyboard, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        if (keys.Count == 0)
        {
            return;
        }

        foreach (string key in keys)
        {
            await keyboard.DownAsync(key).ConfigureAwait(false);
            int delay = _random.Next(_options.MinMouseDelayMs, _options.MaxMouseDelayMs + 1);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        foreach (string key in keys.Reverse())
        {
            await keyboard.UpAsync(key).ConfigureAwait(false);
            int delay = _random.Next(_options.MinMouseDelayMs, _options.MaxMouseDelayMs + 1);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Reset()
    {
        _lastPosition = null;
    }

    private readonly record struct MouseStep(float X, float Y, int DelayMs);

    private static class MousePathGenerator
    {
        public static IReadOnlyList<MouseStep> Generate(
            (float X, float Y) start,
            (float X, float Y) end,
            ComputerUseBehaviorOptions options,
            Random random,
            int viewportWidth,
            int viewportHeight)
        {
            var steps = new List<MouseStep>();
            double distance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
            int segments = Math.Max(6, (int)Math.Ceiling(distance / Math.Max(2, options.MaxMouseStepPixels)));

            (float X, float Y) control1 = (
                Clamp((float)(start.X + (end.X - start.X) * 0.33 + random.NextSingle() * 80 - 40), 0, viewportWidth),
                Clamp((float)(start.Y + random.NextSingle() * 120 - 60), 0, viewportHeight));

            (float X, float Y) control2 = (
                Clamp((float)(end.X - (end.X - start.X) * 0.25 + random.NextSingle() * 80 - 40), 0, viewportWidth),
                Clamp((float)(end.Y + random.NextSingle() * 120 - 60), 0, viewportHeight));

            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                (float X, float Y) point = Bezier(start, control1, control2, end, t);
                int delay = random.Next(options.MinMouseDelayMs, options.MaxMouseDelayMs + 1);
                steps.Add(new MouseStep(point.X, point.Y, delay));
            }

            return steps;
        }

        private static (float X, float Y) Bezier((float X, float Y) p0, (float X, float Y) p1, (float X, float Y) p2, (float X, float Y) p3, float t)
        {
            float u = 1 - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            float x = uuu * p0.X;
            x += 3 * uu * t * p1.X;
            x += 3 * u * tt * p2.X;
            x += ttt * p3.X;

            float y = uuu * p0.Y;
            y += 3 * uu * t * p1.Y;
            y += 3 * u * tt * p2.Y;
            y += ttt * p3.Y;

            return (x, y);
        }

        private static float Clamp(float value, int min, int max)
            => Math.Max(min, Math.Min(max, value));
    }
}
