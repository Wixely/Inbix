using System.Globalization;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Core.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Inbix.Worker;

/// <summary>
/// Rebuilds message index entries from the raw MIME store. Used to recover mail after the index
/// (SQLite DB / JSON store) was lost while <c>/data/raw</c> survived. For every raw file that no message
/// references, it re-creates a row routed from the message headers and leaves it unparsed so the normal
/// parser worker fills in subject/body/attachments. Already-indexed raw files are skipped.
/// </summary>
public sealed class RawReindexer : IRawReindexer
{
    private readonly IRawMessageStore _rawStore;
    private readonly IMessageRepository _messages;
    private readonly IAliasRepository _aliases;
    private readonly HashSet<string> _domains;
    private readonly ILogger<RawReindexer> _logger;

    public RawReindexer(
        IRawMessageStore rawStore, IMessageRepository messages, IAliasRepository aliases,
        IOptions<InbixOptions> options, ILogger<RawReindexer> logger)
    {
        _rawStore = rawStore;
        _messages = messages;
        _aliases = aliases;
        _domains = options.Value.Domains
            .Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).ToHashSet();
        _logger = logger;
    }

    public async Task<ReindexResult> ReindexAsync(CancellationToken ct = default)
    {
        var indexed = new HashSet<string>(
            await _messages.ListRawStoragePathsAsync(ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        var catchAll = await _aliases.GetCatchAllAsync(ct).ConfigureAwait(false);

        int recovered = 0, skipped = 0, failed = 0;
        foreach (var key in _rawStore.EnumerateRawKeys())
        {
            ct.ThrowIfCancellationRequested();
            if (indexed.Contains(key)) { skipped++; continue; }

            try
            {
                long size;
                MimeMessage mime;
                await using (var stream = await _rawStore.OpenReadAsync(key, ct).ConfigureAwait(false))
                {
                    size = stream.CanSeek ? stream.Length : 0;
                    mime = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
                }

                var (aliasId, recipient) = await ResolveAsync(mime, catchAll, ct).ConfigureAwait(false);
                if (aliasId == 0) // no alias and no catch-all to route to
                {
                    failed++;
                    continue;
                }

                await _messages.CreateAsync(new Message
                {
                    AliasId = aliasId,
                    Recipient = recipient,
                    Sender = mime.From?.Mailboxes?.FirstOrDefault()?.Address ?? mime.Sender?.Address,
                    ReceivedAt = ReceivedAt(key, mime),
                    SizeBytes = size,
                    RawStoragePath = key,
                    Parsed = false, // the parser worker fills in subject/body/attachments
                }, ct).ConfigureAwait(false);
                recovered++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Re-index: could not recover raw message {Key}", key);
                failed++;
            }
        }

        _logger.LogInformation("Re-index complete: {Recovered} recovered, {Skipped} already indexed, {Failed} failed",
            recovered, skipped, failed);
        return new ReindexResult(recovered, skipped, failed);
    }

    // Route from the headers: a recipient on an accepted domain → its alias (or catch-all); else catch-all.
    private async Task<(long AliasId, string Recipient)> ResolveAsync(MimeMessage mime, Alias? catchAll, CancellationToken ct)
    {
        var recipients = mime.To.Mailboxes.Concat(mime.Cc.Mailboxes)
            .Select(m => m.Address).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        foreach (var addr in recipients)
        {
            if (!AliasRules.TrySplitAddress(addr, out var local, out var domain)) continue;
            if (_domains.Count > 0 && !_domains.Contains(domain)) continue;

            var alias = await _aliases.FindAsync(local, domain, ct).ConfigureAwait(false);
            if (alias is not null) return (alias.Id, alias.Address);
            if (catchAll is not null) return (catchAll.Id, addr); // accepted domain, unknown alias → catch-all
        }

        // Nothing on an accepted domain — drop into the catch-all so the mail is at least recoverable.
        return catchAll is not null ? (catchAll.Id, recipients.FirstOrDefault() ?? "(unknown)") : (0, "");
    }

    // Prefer the exact receive time encoded in the raw path ("yyyy/MM-dd/HHmmssfff-*.eml"); fall back to the
    // message Date header, then now.
    private static DateTimeOffset ReceivedAt(string key, MimeMessage mime)
    {
        var parts = key.Split('/');
        if (parts.Length >= 3 && parts[^1].Length >= 9 &&
            DateTime.TryParseExact($"{parts[0]}-{parts[1]} {parts[^1][..9]}", "yyyy-MM-dd HHmmssfff",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return new DateTimeOffset(dt, TimeSpan.Zero);

        return mime.Date == default ? DateTimeOffset.UtcNow : mime.Date;
    }
}
