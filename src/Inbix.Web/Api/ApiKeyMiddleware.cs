using Inbix.Core.Options;
using Inbix.Web.Auth;
using Microsoft.Extensions.Options;

namespace Inbix.Web.Api;

/// <summary>
/// Guards /api requests. A request is allowed when it is either authenticated via the login cookie
/// (the browser UI) or presents a matching "X-Api-Key" header (programmatic access). When no admin
/// password and no API key are configured at all, the API is left open for local development.
/// </summary>
public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly string _apiKey;

    public ApiKeyMiddleware(RequestDelegate next, IOptions<InbixOptions> options)
    {
        _next = next;
        _apiKey = options.Value.Admin.ApiKey ?? string.Empty;
    }

    public async Task InvokeAsync(HttpContext context, IAdminAuthenticator auth)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Logged-in browser session.
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // Programmatic access via API key.
        if (_apiKey.Length > 0 &&
            context.Request.Headers.TryGetValue(HeaderName, out var provided) &&
            CryptographicEquals(provided.ToString(), _apiKey))
        {
            await _next(context);
            return;
        }

        // Nothing configured -> open for local dev.
        if (!auth.Enabled && _apiKey.Length == 0)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Authentication required (login cookie or X-Api-Key)." });
    }

    private static bool CryptographicEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
