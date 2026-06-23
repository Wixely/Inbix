using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Inbix.Web.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapInbixAuth(this IEndpointRouteBuilder app)
    {
        // Note: paths differ from the "/login" Blazor page route to avoid an ambiguous endpoint match.
        app.MapPost("/auth/login", async (HttpContext ctx, IAdminAuthenticator auth) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            if (!auth.Validate(username, password))
            {
                var q = string.IsNullOrEmpty(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
                return Results.Redirect($"/login?error=1{q}");
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, auth.Username)],
                CookieAuthenticationDefaults.AuthenticationScheme);

            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous();

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).AllowAnonymous();

        return app;
    }

    /// <summary>Only allow same-site relative redirects to avoid open-redirect abuse.</summary>
    private static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") ? url : "/";
}
