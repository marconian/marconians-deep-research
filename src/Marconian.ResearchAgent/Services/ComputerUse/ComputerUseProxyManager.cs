using System;
using System.Threading;
using Marconian.ResearchAgent.Configuration;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public sealed class ComputerUseProxyManager
{
    private readonly ComputerUseProxyOptions _options;
    private int _counter;

    public ComputerUseProxyManager(ComputerUseProxyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _counter = new Random().Next(0, options.Endpoints.Count > 0 ? options.Endpoints.Count : 1);
    }

    public ComputerUseProxySelection? TryAcquire()
    {
        if (!_options.Enabled || _options.Endpoints.Count == 0)
        {
            return null;
        }

        int index = !_options.RotatePerSession
            ? 0
            : Math.Abs(Interlocked.Increment(ref _counter)) % _options.Endpoints.Count;

        ComputerUseProxyEndpoint endpoint = _options.Endpoints[index];
        var proxy = new Proxy
        {
            Server = endpoint.Uri,
            Username = endpoint.Username,
            Password = endpoint.Password
        };

        return new ComputerUseProxySelection(endpoint, proxy);
    }
}

public sealed record ComputerUseProxySelection(ComputerUseProxyEndpoint Endpoint, Proxy Proxy);
