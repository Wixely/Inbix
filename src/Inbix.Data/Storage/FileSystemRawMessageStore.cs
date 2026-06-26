using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Storage;

/// <summary>
/// Stores raw MIME and attachment bytes on the local file system, sharded by receive date.
/// Returns paths relative to the configured raw root so the database stays portable.
/// </summary>
public sealed class FileSystemRawMessageStore : IRawMessageStore
{
    private readonly string _root;

    public FileSystemRawMessageStore(IOptions<InbixOptions> options)
    {
        _root = Path.GetFullPath(options.Value.Storage.RawPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveRawAsync(ReadOnlyMemory<byte> bytes, DateTimeOffset receivedAt, CancellationToken ct = default)
    {
        var relativeDir = Path.Combine(receivedAt.UtcDateTime.ToString("yyyy"), receivedAt.UtcDateTime.ToString("MM-dd"));
        var fileName = $"{receivedAt.UtcDateTime:HHmmssfff}-{Guid.NewGuid():N}.eml";
        var relativePath = Path.Combine(relativeDir, fileName);

        await WriteAsync(relativePath, bytes, ct).ConfigureAwait(false);
        return ToPosix(relativePath);
    }

    public async Task<string> SaveAttachmentAsync(string storageKeyHint, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var safe = MakeSafe(storageKeyHint);
        var relativePath = Path.Combine("attachments", $"{Guid.NewGuid():N}-{safe}");
        await WriteAsync(relativePath, bytes, ct).ConfigureAwait(false);
        return ToPosix(relativePath);
    }

    public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
    {
        var full = ResolveInsideRoot(storagePath);
        Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(storagePath))
        {
            try
            {
                var full = ResolveInsideRoot(storagePath);
                if (File.Exists(full)) File.Delete(full);
            }
            catch
            {
                // Best-effort: a missing or locked file must not block message deletion.
            }
        }
        return Task.CompletedTask;
    }

    private async Task WriteAsync(string relativePath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var full = ResolveInsideRoot(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    private string ResolveInsideRoot(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        // Guard against path traversal escaping the raw root. Compare against the root *with* a
        // trailing separator so a sibling like "<root>-evil" cannot pass a naive prefix check.
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!string.Equals(full, _root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved storage path escapes the raw storage root.");
        return full;
    }

    private static string ToPosix(string path) => path.Replace('\\', '/');

    private static string MakeSafe(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "attachment";
        var cleaned = Path.GetFileName(name);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "attachment" : cleaned;
    }
}
