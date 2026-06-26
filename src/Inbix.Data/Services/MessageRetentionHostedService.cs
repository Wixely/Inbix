using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Services;

/// <summary>
/// Retention cleanup on a schedule (a few seconds after startup, then every
/// Inbix:Junk:CleanupIntervalHours). Two passes: it deletes junked mail older than
/// Inbix:Junk:RetentionDays, and for each mailbox with expiry enabled it deletes non-junked mail
/// past that mailbox's retention — measured from the message's last state change (junk/unjunk/
/// sweep/unsweep) or, if it was never moved, its received date. Hard-deletes rows + raw/attachment files.
/// </summary>
public sealed class MessageRetentionHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private readonly IMessageRepository _messages;
    private readonly IAliasRepository _aliases;
    private readonly IAuditRepository _audit;
    private readonly JunkOptions _options;
    private readonly ILogger<MessageRetentionHostedService> _logger;

    public MessageRetentionHostedService(
        IMessageRepository messages, IAliasRepository aliases, IAuditRepository audit,
        IOptions<InbixOptions> options, ILogger<MessageRetentionHostedService> logger)
    {
        _messages = messages;
        _aliases = aliases;
        _audit = audit;
        _options = options.Value.Junk;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        await RunOnceAsync(stoppingToken).ConfigureAwait(false);

        var hours = _options.CleanupIntervalHours;
        if (hours <= 0)
        {
            _logger.LogInformation("Retention cleanup ran at startup; periodic runs disabled (CleanupIntervalHours <= 0).");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var deleted = 0;
        try
        {
            // Junk retention.
            var junkCutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, _options.RetentionDays));
            foreach (var id in await _messages.ListJunkedBeforeAsync(junkCutoff, ct).ConfigureAwait(false))
            {
                await _messages.DeleteAsync(id, ct).ConfigureAwait(false);
                deleted++;
            }

            // Per-mailbox expiry.
            var now = DateTimeOffset.UtcNow;
            foreach (var alias in await _aliases.ListAsync(ct).ConfigureAwait(false))
            {
                if (!alias.ExpiryEnabled) continue;
                var cutoff = now.AddDays(-Math.Max(1, alias.ExpiryDays));
                foreach (var m in await _messages.ListExpiryCandidatesAsync(alias.Id, cutoff, ct).ConfigureAwait(false))
                {
                    await _messages.DeleteAsync(m.Id, ct).ConfigureAwait(false);
                    deleted++;
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Retention cleanup removed {Count} message(s).", deleted);
                await _audit.WriteAsync(new AuditEntry
                {
                    Action = "retention.cleanup", TargetType = "message", TargetId = deleted.ToString(),
                    Actor = "job", CreatedAt = DateTimeOffset.UtcNow
                }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention cleanup run failed.");
        }
    }
}
