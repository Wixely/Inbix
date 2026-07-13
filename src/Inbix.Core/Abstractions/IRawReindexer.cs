namespace Inbix.Core.Abstractions;

/// <summary>Outcome of a re-index pass over the raw message store.</summary>
/// <param name="Recovered">Raw messages that had no index entry and were re-created.</param>
/// <param name="Skipped">Raw messages already present in the index (left untouched).</param>
/// <param name="Failed">Raw messages that could not be read/routed.</param>
public readonly record struct ReindexResult(int Recovered, int Skipped, int Failed)
{
    public int Total => Recovered + Skipped + Failed;
}

/// <summary>
/// Recovers messages from the raw MIME store when the index (SQLite DB / JSON store) is missing entries
/// — e.g. after the store folder was lost but <c>/data/raw</c> survived. Best-effort: routing (which
/// mailbox) and the receive time are reconstructed from message headers, so recovered mail may land in
/// the catch-all if its original alias can't be determined. Existing indexed messages are never touched.
/// </summary>
public interface IRawReindexer
{
    Task<ReindexResult> ReindexAsync(CancellationToken ct = default);
}
