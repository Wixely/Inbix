using Microsoft.Extensions.DependencyInjection;
using SmtpServer.Storage;

namespace Inbix.Smtp;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the SMTP receiver: the alias mailbox filter, the inbound message store, the
    /// factories SmtpServer resolves from DI, and the listener hosted service.
    /// </summary>
    public static IServiceCollection AddInbixSmtp(this IServiceCollection services)
    {
        services.AddSingleton<AliasMailboxFilter>();
        services.AddSingleton<InbixMessageStore>();

        // SmtpServer resolves these factories from the application's IServiceProvider.
        services.AddSingleton<IMailboxFilterFactory>(sp =>
            new DelegatingMailboxFilterFactory(_ => sp.GetRequiredService<AliasMailboxFilter>()));
        services.AddSingleton<IMessageStoreFactory>(sp =>
            new DelegatingMessageStoreFactory(_ => sp.GetRequiredService<InbixMessageStore>()));

        services.AddHostedService<SmtpServerHostedService>();

        return services;
    }
}
