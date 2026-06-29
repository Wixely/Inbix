using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Identities;
using Inbix.Core.Options;
using Inbix.Data.Dapper;
using Inbix.Data.Json;
using Inbix.Data.Json.Repositories;
using Inbix.Data.Migrations;
using Inbix.Data.Repositories;
using Inbix.Data.Services;
using Inbix.Data.Sqlite;
using Inbix.Data.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Inbix.Data;

public static class DependencyInjection
{
    /// <summary>Registers the data layer with the default SQLite provider.</summary>
    public static IServiceCollection AddInbixData(this IServiceCollection services) =>
        AddInbixData(services, "sqlite");

    /// <summary>Registers the data layer, selecting the storage provider from <c>Inbix:Database:Provider</c>.</summary>
    public static IServiceCollection AddInbixData(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration[$"{InbixOptions.SectionName}:Database:Provider"];
        return AddInbixData(services, provider);
    }

    /// <summary>
    /// Registers the data layer for a specific provider: <c>"sqlite"</c> (default) for the embedded SQL
    /// database, or <c>"json"</c> for the file/folder JSON store. Repositories, the inbound sink, alias
    /// resolver and background services are otherwise the same.
    /// </summary>
    public static IServiceCollection AddInbixData(this IServiceCollection services, string? provider)
    {
        // Dapper global config: map snake_case columns to PascalCase properties + ISO timestamps.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new DateOnlyHandler());

        provider = string.IsNullOrWhiteSpace(provider) ? "sqlite" : provider.Trim().ToLowerInvariant();

        if (provider == "json")
            AddJsonProvider(services);
        else
            AddSqlProvider(services, provider);

        // --- Provider-agnostic services (depend only on the repository interfaces) ---
        services.AddSingleton<IAliasService, AliasService>();
        services.AddSingleton<IBlacklistMatcher, CachingBlacklistMatcher>();
        services.AddSingleton<IBlacklistService, BlacklistService>();
        services.AddSingleton<IIdentityService, IdentityService>();
        services.AddSingleton<IIdentityGenerator, RandomIdentityGenerator>();
        services.AddSingleton<IRawMessageStore, FileSystemRawMessageStore>();
        services.AddSingleton<IAliasResolver, CachingAliasResolver>();
        services.AddSingleton<IInboundMessageSink, InboundMessageSink>();
        services.AddHostedService<SampleDataSeeder>();
        services.AddHostedService<MessageRetentionHostedService>();

        return services;
    }

    private static void AddSqlProvider(IServiceCollection services, string provider)
    {
        services.AddSingleton<IDbConnectionFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<InbixOptions>>();
            var p = options.Value.Database.Provider?.Trim().ToLowerInvariant();
            return p switch
            {
                "sqlite" or "" or null => new SqliteConnectionFactory(options),
                _ => throw new NotSupportedException(
                    $"Database provider '{p}' is not supported. Use 'sqlite' or 'json'.")
            };
        });

        services.AddSingleton<IMigrationRunner, ManifestMigrationRunner>();
        // Migrations apply before any other hosted service (seeder, backup, SMTP, worker) — registration order.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MigrationHostedService>());

        services.AddSingleton<IAliasRepository, AliasRepository>();
        services.AddSingleton<IMessageRepository, MessageRepository>();
        services.AddSingleton<ISmtpSessionRepository, SmtpSessionRepository>();
        services.AddSingleton<IAuditRepository, AuditRepository>();
        services.AddSingleton<IBlacklistRuleRepository, BlacklistRuleRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IIdentityRepository, IdentityRepository>();

        services.AddSingleton<IBackupService, SqliteBackupService>();
        services.AddHostedService<BackupHostedService>();
        services.AddSingleton<IReloadableStore, NoOpReloadableStore>();
    }

    private static void AddJsonProvider(IServiceCollection services)
    {
        services.AddSingleton<JsonDataStore>();
        services.AddSingleton<IReloadableStore>(sp => sp.GetRequiredService<JsonDataStore>());
        // Load the store into memory before any other hosted service runs — registration order.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JsonStoreInitHostedService>());

        services.AddSingleton<IAliasRepository, JsonAliasRepository>();
        services.AddSingleton<IMessageRepository, JsonMessageRepository>();
        services.AddSingleton<ISmtpSessionRepository, JsonSmtpSessionRepository>();
        services.AddSingleton<IAuditRepository, JsonAuditRepository>();
        services.AddSingleton<IBlacklistRuleRepository, JsonBlacklistRuleRepository>();
        services.AddSingleton<ISettingsRepository, JsonSettingsRepository>();
        services.AddSingleton<IIdentityRepository, JsonIdentityRepository>();

        // The JSON files are their own backup unit; no in-app backup / scheduled backup service.
        services.AddSingleton<IBackupService, NullBackupService>();
    }
}
