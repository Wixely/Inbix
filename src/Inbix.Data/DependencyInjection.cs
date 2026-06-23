using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Inbix.Data.Dapper;
using Inbix.Data.Migrations;
using Inbix.Data.Repositories;
using Inbix.Data.Services;
using Inbix.Data.Sqlite;
using Inbix.Data.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Inbix.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the data layer: connection factory (provider-selected), migration runner,
    /// repositories, raw store, alias resolver and inbound sink.
    /// </summary>
    public static IServiceCollection AddInbixData(this IServiceCollection services)
    {
        // Dapper global config: map snake_case columns to PascalCase properties + ISO timestamps.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());

        services.AddSingleton<IDbConnectionFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<InbixOptions>>();
            var provider = options.Value.Database.Provider?.Trim().ToLowerInvariant();
            return provider switch
            {
                "sqlite" or "" or null => new SqliteConnectionFactory(options),
                _ => throw new NotSupportedException(
                    $"Database provider '{provider}' is not supported yet. Implement IDbConnectionFactory for it.")
            };
        });

        services.AddSingleton<IMigrationRunner, ManifestMigrationRunner>();

        services.AddSingleton<IAliasRepository, AliasRepository>();
        services.AddSingleton<IMessageRepository, MessageRepository>();
        services.AddSingleton<ISmtpSessionRepository, SmtpSessionRepository>();
        services.AddSingleton<IAuditRepository, AuditRepository>();

        services.AddSingleton<IRawMessageStore, FileSystemRawMessageStore>();
        services.AddSingleton<IAliasResolver, CachingAliasResolver>();
        services.AddSingleton<IInboundMessageSink, InboundMessageSink>();

        // Run migrations before SMTP/worker hosted services start.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, MigrationHostedService>());

        return services;
    }
}
