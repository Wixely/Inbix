using Inbix.Core.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;

namespace Inbix.Web.Health;

/// <summary>Readiness check: confirms storage is reachable. For SQL providers it runs a trivial query; the
/// JSON store is in-memory and always reachable once loaded.</summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbConnectionFactory? _factory;

    public DatabaseHealthCheck(IServiceProvider services) => _factory = services.GetService<IDbConnectionFactory>();

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_factory is null)
            return HealthCheckResult.Healthy("JSON store (in memory).");

        try
        {
            await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Database reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
