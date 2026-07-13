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
        return RetryAsync<Stream>(() => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read), ct);
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

    public IReadOnlyList<string> EnumerateRawKeys()
    {
        // Raw MIME files are written as "*.eml"; attachments have no extension, so this excludes them.
        if (!Directory.Exists(_root)) return [];
        var keys = new List<string>();
        foreach (var full in Directory.EnumerateFiles(_root, "*.eml", SearchOption.AllDirectories))
            keys.Add(ToPosix(Path.GetRelativePath(_root, full)));
        return keys;
    }

    private Task WriteAsync(string relativePath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var full = ResolveInsideRoot(relativePath);
        // FileMode.Create (not CreateNew) so a retry after a partial write is idempotent; the path is a
        // unique GUID so there is nothing to clobber.
        return RetryAsync(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
            await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    // Network filesystems (NFS/SMB) throw transient IO errors — notably ESTALE ("stale file handle") —
    // that succeed once the client re-resolves the path. Retry a few times with a short backoff.
    private const int MaxIoAttempts = 4;

    private static async Task RetryAsync(Func<Task> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await action().ConfigureAwait(false); return; }
            catch (IOException) when (attempt < MaxIoAttempts)
            {
                await Task.Delay(attempt * 150, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> RetryAsync<T>(Func<T> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return action(); }
            catch (IOException) when (attempt < MaxIoAttempts)
            {
                await Task.Delay(attempt * 150, ct).ConfigureAwait(false);
            }
        }
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
