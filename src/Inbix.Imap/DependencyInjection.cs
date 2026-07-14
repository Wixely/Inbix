using Microsoft.Extensions.DependencyInjection;

namespace Inbix.Imap;

public static class DependencyInjection
{
    /// <summary>Registers the read-only IMAP server. The hosted service no-ops unless <c>Inbix:Imap:Enabled</c>.</summary>
    public static IServiceCollection AddInbixImap(this IServiceCollection services)
    {
        services.AddSingleton<ImapMailboxProvider>();
        services.AddHostedService<ImapServerHostedService>();
        return services;
    }
}
