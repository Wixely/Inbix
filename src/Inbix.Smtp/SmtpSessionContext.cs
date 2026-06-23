using System.Net;
using SmtpServer;
using SmtpServer.Net;

namespace Inbix.Smtp;

internal static class SmtpSessionContext
{
    /// <summary>Best-effort remote IP for a session, or null if unavailable.</summary>
    public static string? GetRemoteIp(ISessionContext context)
    {
        if (context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value) && value is IPEndPoint ep)
            return ep.Address.ToString();
        return null;
    }
}
