namespace Inbix.Core.Abstractions;

/// <summary>
/// Fast recipient-acceptance check used during the SMTP RCPT TO phase.
/// Kept separate from <see cref="IAliasRepository"/> so it can be cached aggressively.
/// </summary>
public interface IAliasResolver
{
    /// <summary>True if the full address is a known, enabled alias on an accepted domain.</summary>
    Task<bool> IsDeliverableAsync(string address, CancellationToken ct = default);
}
