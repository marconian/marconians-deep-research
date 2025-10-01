using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Marconian.ResearchAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Services.Caching;

public sealed class RedisCacheService : IRedisCacheService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(Settings.AppSettings settings, ILogger<RedisCacheService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? NullLogger<RedisCacheService>.Instance;

        _cache = new RedisCache(new RedisCacheOptions
        {
            Configuration = settings.RedisConnectionString
        });
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        byte[]? payload = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (payload is null || payload.Length == 0)
        {
            _logger.LogTrace("Cache miss for key {Key}.", key);
            return default;
        }

        try
        {
            T? result = JsonSerializer.Deserialize<T>(payload, SerializerOptions);
            _logger.LogTrace("Cache hit for key {Key}.", key);
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize cache entry for key {Key}. Evicting.", key);
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        var options = new DistributedCacheEntryOptions();
        if (ttl.HasValue)
        {
            options.AbsoluteExpirationRelativeToNow = ttl;
        }

        await _cache.SetAsync(key, payload, options, cancellationToken).ConfigureAwait(false);
        _logger.LogTrace("Stored key {Key} in Redis with TTL {Ttl}.", key, ttl);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        _logger.LogTrace("Removed key {Key} from Redis cache.", key);
    }

    public ValueTask DisposeAsync()
    {
        if (_cache is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        if (_cache is IDisposable disposable)
        {
            disposable.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
