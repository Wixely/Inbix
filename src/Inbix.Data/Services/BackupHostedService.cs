using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Services;

/// <summary>Runs scheduled database backups when <c>Inbix:Backups:Enabled</c> is set.</summary>
public sealed class BackupHostedService : BackgroundService
{
    private readonly IBackupService _backup;
    private readonly BackupOptions _options;
    private readonly ILogger<BackupHostedService> _logger;

    public BackupHostedService(IBackupService backup, IOptions<InbixOptions> options, ILogger<BackupHostedService> logger)
    {
        _backup = backup;
        _options = options.Value.Backups;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        var interval = TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));
        _logger.LogInformation("Scheduled backups enabled (every {Hours}h, keep {Keep})", interval.TotalHours, _options.RetentionCount);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _backup.CreateBackupAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled backup failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
