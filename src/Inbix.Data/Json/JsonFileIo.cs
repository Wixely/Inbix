using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inbix.Data.Json;

/// <summary>
/// Resilient JSON file primitives for the file/folder store. Every write goes to a temporary file and is
/// then atomically renamed over the target, so a crash or a lying <c>fsync</c> on a network filesystem
/// damages at most the in-flight temp file — never the existing record. Transient IO errors (notably NFS
/// ESTALE "stale file handle") are retried with backoff up to a configured timeout.
/// </summary>
public sealed class JsonFileIo
{
    private readonly TimeSpan _retryFor;

    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true, // skip computed getters (Address, FullName, …) so files stay clean
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Single-line serialization for append-only logs (one JSON object per line).</summary>
    public static readonly JsonSerializerOptions CompactOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonFileIo(TimeSpan retryFor) => _retryFor = retryFor;

    /// <summary>Serialize <paramref name="value"/> to <paramref name="fullPath"/> via a temp file + atomic rename.</summary>
    public Task WriteAsync<T>(string fullPath, T value, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        return RetryAsync(async () =>
        {
            var dir = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }
                File.Move(tmp, fullPath, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }, ct);
    }

    /// <summary>Read and deserialize a JSON file. Returns <c>default</c> when the file does not exist.</summary>
    public Task<T?> ReadAsync<T>(string fullPath, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            if (!File.Exists(fullPath)) return default;
            await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<T>(fs, SerializerOptions, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Atomically move/rename a file (overwriting the destination).</summary>
    public Task MoveAsync(string from, string to, CancellationToken ct = default) =>
        RetryAsync(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
            return Task.CompletedTask;
        }, ct);

    /// <summary>Append a single line to a text file (used for the append-only audit log).</summary>
    public Task AppendLineAsync(string fullPath, string line, CancellationToken ct = default) =>
        RetryAsync(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.AppendAllTextAsync(fullPath, line + "\n", Encoding.UTF8, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Best-effort delete; never throws.</summary>
    public void TryDelete(string fullPath)
    {
        try { if (File.Exists(fullPath)) File.Delete(fullPath); }
        catch { /* a missing or locked file must not block the operation */ }
    }

    private async Task RetryAsync(Func<Task> action, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try { await action().ConfigureAwait(false); return; }
            catch (IOException) when (sw.Elapsed < _retryFor)
            {
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (IOException) when (sw.Elapsed < _retryFor)
            {
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(500, attempt * 100));
}
