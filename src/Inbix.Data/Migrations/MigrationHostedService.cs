using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Migrations;

/// <summary>Runs pending migrations on startup (before other hosted services begin work).</summary>
public sealed class MigrationHostedService : IHostedService
{
    private readonly IMigrationRunner _runner;
    private readonly InbixOptions _options;

    public MigrationHostedService(IMigrationRunner runner, IOptions<InbixOptions> options)
    {
        _runner = runner;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Database.MigrateOnStartup)
            await _runner.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
