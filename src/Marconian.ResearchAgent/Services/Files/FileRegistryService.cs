using System.Linq;
using System.Text.Json;
using Marconian.ResearchAgent.Models.Files;

namespace Marconian.ResearchAgent.Services.Files;

public sealed class FileRegistryService : IFileRegistryService
{
    private readonly string _baseDirectory;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FileRegistryService(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory is null
            ? Path.Combine(AppContext.BaseDirectory, "data", "registry")
            : Path.GetFullPath(baseDirectory);

        Directory.CreateDirectory(_baseDirectory);
    }

    public async Task<FileRegistryEntry> SaveFileAsync(
        string researchSessionId,
        string originalFileName,
        Stream content,
        string? contentType,
        string? sourceUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(researchSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentNullException.ThrowIfNull(content);

        string sessionDirectory = EnsureSessionDirectory(researchSessionId);
        FileRegistryManifest manifest = await ReadManifestAsync(sessionDirectory, cancellationToken).ConfigureAwait(false);

        string sanitizedFileName = Path.GetFileName(originalFileName);
        string fileId = Guid.NewGuid().ToString("N");
        string storedFileName = $"{fileId}_{sanitizedFileName}";
        string relativePath = Path.Combine(researchSessionId, storedFileName);
        string absolutePath = Path.Combine(_baseDirectory, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await using (var fileStream = File.Create(absolutePath))
        {
            await content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        var entry = new FileRegistryEntry
        {
            FileId = fileId,
            FileName = sanitizedFileName,
            RelativePath = relativePath,
            SourceUrl = sourceUrl,
            ContentType = contentType,
            DownloadedAtUtc = DateTimeOffset.UtcNow
        };

        manifest.Files.RemoveAll(existing => existing.FileId == entry.FileId);
        manifest.Files.Add(entry);
        await WriteManifestAsync(sessionDirectory, manifest, cancellationToken).ConfigureAwait(false);

        return entry;
    }

    public async Task<Stream?> OpenFileAsync(string researchSessionId, string fileId, CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(researchSessionId, fileId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        string absolutePath = Path.Combine(_baseDirectory, entry.RelativePath);
        if (!File.Exists(absolutePath))
        {
            return null;
        }

        return File.OpenRead(absolutePath);
    }

    public async Task<FileRegistryEntry?> GetEntryAsync(string researchSessionId, string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(researchSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        string sessionDirectory = EnsureSessionDirectory(researchSessionId);
        FileRegistryManifest manifest = await ReadManifestAsync(sessionDirectory, cancellationToken).ConfigureAwait(false);
        return manifest.Files.FirstOrDefault(entry => entry.FileId.Equals(fileId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<FileRegistryEntry>> ListEntriesAsync(string researchSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(researchSessionId);
        string sessionDirectory = EnsureSessionDirectory(researchSessionId);
        FileRegistryManifest manifest = await ReadManifestAsync(sessionDirectory, cancellationToken).ConfigureAwait(false);
        return manifest.Files.OrderByDescending(entry => entry.DownloadedAtUtc).ToList();
    }

    private string EnsureSessionDirectory(string researchSessionId)
    {
        string sessionDirectory = Path.Combine(_baseDirectory, researchSessionId);
        Directory.CreateDirectory(sessionDirectory);
        return sessionDirectory;
    }

    private async Task<FileRegistryManifest> ReadManifestAsync(string sessionDirectory, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(sessionDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new FileRegistryManifest();
        }

        await using FileStream stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<FileRegistryManifest>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false)
               ?? new FileRegistryManifest();
    }

    private async Task WriteManifestAsync(string sessionDirectory, FileRegistryManifest manifest, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(sessionDirectory, "manifest.json");
        await using FileStream stream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(stream, manifest, _serializerOptions, cancellationToken).ConfigureAwait(false);
    }
}
