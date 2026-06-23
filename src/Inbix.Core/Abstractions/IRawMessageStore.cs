namespace Inbix.Core.Abstractions;

/// <summary>
/// Stores raw MIME and attachment bytes outside the database so the DB stays small and
/// parsing can be re-run later from the original source.
/// </summary>
public interface IRawMessageStore
{
    /// <summary>Persist raw MIME bytes. Returns a provider-relative storage path/key.</summary>
    Task<string> SaveRawAsync(ReadOnlyMemory<byte> bytes, DateTimeOffset receivedAt, CancellationToken ct = default);

    /// <summary>Persist an attachment's bytes. Returns a provider-relative storage path/key.</summary>
    Task<string> SaveAttachmentAsync(string storageKeyHint, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

    /// <summary>Open a stream over previously stored bytes.</summary>
    Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default);
}
