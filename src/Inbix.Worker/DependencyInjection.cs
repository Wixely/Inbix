using Inbix.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Inbix.Worker;

public static class DependencyInjection
{
    /// <summary>Registers the background MIME parser worker and the raw-store re-indexer.</summary>
    public static IServiceCollection AddInbixWorker(this IServiceCollection services)
    {
        services.AddHostedService<MimeParserWorker>();
        services.AddSingleton<IRawReindexer, RawReindexer>();
        return services;
    }
}
