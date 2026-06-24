using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Web.Diagnostics;

/// <summary>
/// Runs the status-page diagnostics automatically: once a few seconds after startup (so migrations
/// and listeners are up), then on a configurable interval (Inbix:Diagnostics:IntervalHours, default
/// 6h). Results are cached on the singleton <see cref="DiagnosticsService"/> and surfaced on the
/// Status page and the sidebar status indicator. Set IntervalHours &lt;= 0 to run only at startup.
/// </summary>
public sealed class DiagnosticsHostedService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly DiagnosticsService _diagnostics;
    private readonly InbixOptions _options;
    private readonly ILogger<DiagnosticsHostedService> _logger;

    public DiagnosticsHostedService(
        DiagnosticsService diagnostics, IOptions<InbixOptions> options, ILogger<DiagnosticsHostedService> logger)
    {
        _diagnostics = diagnostics;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        await RunOnceAsync(stoppingToken);

        var hours = _options.Diagnostics.IntervalHours;
        if (hours <= 0)
        {
            _logger.LogInformation("Background diagnostics ran at startup; periodic runs disabled (IntervalHours <= 0).");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var results = await _diagnostics.RunAllAsync(ct);
            var errors = results.Count(r => r.Status == DiagnosticStatus.Error);
            var warnings = results.Count(r => r.Status == DiagnosticStatus.Warning);
            _logger.LogInformation("Background diagnostics complete: {Errors} failure(s), {Warnings} warning(s).", errors, warnings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background diagnostics run failed.");
        }
    }
}
