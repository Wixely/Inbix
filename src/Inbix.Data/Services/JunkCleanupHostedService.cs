using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Services;

/// <summary>
/// Prunes the Junk inbox on a schedule: a few seconds after startup, then every
/// Inbix:Junk:CleanupIntervalHours, it hard-deletes messages whose junked-at is older than
/// Inbix:Junk:RetentionDays (rows + bodies/attachments + raw files). Mirrors DiagnosticsHostedService.
/// </summary>
public sealed class JunkCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private readonly IMessageRepository _messages;
    private readonly IAuditRepository _audit;
    private readonly JunkOptions _options;
    private readonly ILogger<JunkCleanupHostedService> _logger;

    public JunkCleanupHostedService(
        IMessageRepository messages, IAuditRepository audit,
        IOptions<InbixOptions> options, ILogger<JunkCleanupHostedService> logger)
    {
        _messages = messages;
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
            _logger.LogInformation("Junk cleanup ran at startup; periodic runs disabled (CleanupIntervalHours <= 0).");
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
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, _options.RetentionDays));
            var ids = await _messages.ListJunkedBeforeAsync(cutoff, ct).ConfigureAwait(false);
            foreach (var id in ids)
                await _messages.DeleteAsync(id, ct).ConfigureAwait(false);

            if (ids.Count > 0)
            {
                _logger.LogInformation("Junk cleanup removed {Count} message(s) older than {Days} day(s).", ids.Count, _options.RetentionDays);
                await _audit.WriteAsync(new AuditEntry
                {
                    Action = "junk.cleanup", TargetType = "junk", TargetId = ids.Count.ToString(),
                    Actor = "job", CreatedAt = DateTimeOffset.UtcNow
                }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Junk cleanup run failed.");
        }
    }
}
