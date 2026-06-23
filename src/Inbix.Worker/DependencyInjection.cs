using Microsoft.Extensions.DependencyInjection;

namespace Inbix.Worker;

public static class DependencyInjection
{
    /// <summary>Registers the background MIME parser worker.</summary>
    public static IServiceCollection AddInbixWorker(this IServiceCollection services)
    {
        services.AddHostedService<MimeParserWorker>();
        return services;
    }
}
