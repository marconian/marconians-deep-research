using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Services.Caching;

public sealed class HybridCacheService : ICacheService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IMemoryCache _memoryCache;
    private readonly string _cacheDirectory;
    private readonly ILogger<HybridCacheService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public HybridCacheService(string cacheDirectory, ILogger<HybridCacheService>? logger = null, IMemoryCache? memoryCache = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(_cacheDirectory);

        _logger = logger ?? NullLogger<HybridCacheService>.Instance;
        _memoryCache = memoryCache ?? new MemoryCache(new MemoryCacheOptions());
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_memoryCache.TryGetValue(key, out var cached) && cached is T typed)
        {
            _logger.LogTrace("Cache hit (memory) for key {Key}.", key);
            return typed;
        }

        string filePath = GetFilePath(key);
        if (!File.Exists(filePath))
        {
            _logger.LogTrace("Cache miss for key {Key}.", key);
            return default;
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            CacheEnvelope<T>? envelope = await JsonSerializer.DeserializeAsync<CacheEnvelope<T>>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                _logger.LogWarning("Failed to deserialize cache entry for key {Key}; removing file.", key);
                SafeDelete(filePath);
                return default;
            }

            if (envelope.ExpiresAtUtc is DateTimeOffset expires && expires <= DateTimeOffset.UtcNow)
            {
                _logger.LogTrace("Cache entry for key {Key} expired at {Expiration}. Removing.", key, expires);
                _memoryCache.Remove(key);
                SafeDelete(filePath);
                return default;
            }

            if (envelope.Value is null)
            {
                return default;
            }

            SetMemoryCache(key, envelope.Value, envelope.ExpiresAtUtc);
            _logger.LogTrace("Cache hit (disk) for key {Key}.", key);
            return envelope.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to materialize cache entry for key {Key} from disk. Evicting file.", key);
            _memoryCache.Remove(key);
            SafeDelete(filePath);
            return default;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        DateTimeOffset? expiresAt = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : null;
        SetMemoryCache(key, value, expiresAt);

        string filePath = GetFilePath(key);
        var envelope = new CacheEnvelope<T>
        {
            ExpiresAtUtc = expiresAt,
            Value = value
        };

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, envelope, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to write cache entry for key {Key} to disk.", key);
        }
        finally
        {
            _fileLock.Release();
        }

        _logger.LogTrace("Stored key {Key} in hybrid cache with TTL {Ttl}.", key, ttl);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _memoryCache.Remove(key);
        string filePath = GetFilePath(key);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeDelete(filePath);
        }
        finally
        {
            _fileLock.Release();
        }

        _logger.LogTrace("Removed key {Key} from hybrid cache.", key);
    }

    public ValueTask DisposeAsync()
    {
        _memoryCache.Dispose();
        _fileLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SetMemoryCache<T>(string key, T value, DateTimeOffset? expiresAt)
    {
        if (expiresAt.HasValue)
        {
            _memoryCache.Set(key, value!, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expiresAt
            });
        }
        else
        {
            _memoryCache.Set(key, value!);
        }
    }

    private string GetFilePath(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string fileName = Convert.ToHexString(hash) + ".json";
        return Path.Combine(_cacheDirectory, fileName);
    }

    private void SafeDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to delete cache file {Path}.", filePath);
        }
    }

    private sealed class CacheEnvelope<TValue>
    {
        public DateTimeOffset? ExpiresAtUtc { get; set; }

        public TValue? Value { get; set; }
    }
}
