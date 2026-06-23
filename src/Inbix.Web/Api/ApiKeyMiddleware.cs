using Inbix.Core.Options;
using Microsoft.Extensions.Options;

namespace Inbix.Web.Api;

/// <summary>
/// Optional API-key gate for /api routes. When <see cref="AdminOptions.ApiKey"/> is configured,
/// requests must present a matching "X-Api-Key" header. Empty config disables the check (local dev).
/// This is a minimal MVP control; see the plan's hardening phase for proper admin auth.
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

    public async Task InvokeAsync(HttpContext context)
    {
        if (_apiKey.Length == 0 || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var provided) ||
            !CryptographicEquals(provided.ToString(), _apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key." });
            return;
        }

        await _next(context);
    }

    private static bool CryptographicEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
