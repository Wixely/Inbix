using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Smtp;
using Inbix.Web.Api;
using Inbix.Web.Auth;
using Inbix.Web.Components;
using Inbix.Worker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

// CLI helper: generate a PBKDF2 hash for Inbix:Admin:PasswordHash, then exit.
//   dotnet run --project src/Inbix.Web -- hash-password "my secret"
if (args is ["hash-password", var pw])
{
    Console.WriteLine(PasswordHasher.Hash(pw));
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Allow running as a Windows Service (no-op when launched normally or in a container).
builder.Host.UseWindowsService(o => o.ServiceName = "Inbix");

// Configuration: bind the "Inbix" section. Every value is overridable via environment variables,
// e.g. Inbix__Smtp__Port=2525 or Inbix__Database__ConnectionString=...
builder.Services.AddOptions<InbixOptions>()
    .Bind(builder.Configuration.GetSection(InbixOptions.SectionName))
    .ValidateOnStart();

// Application layers.
builder.Services.AddInbixData();   // SQLite + Dapper + migrations (provider-swappable)
builder.Services.AddInbixSmtp();   // SmtpServer receiver
builder.Services.AddInbixWorker(); // MIME parser background worker

// --- Authentication / authorization ---
var adminConfig = builder.Configuration.GetSection("Inbix:Admin").Get<AdminOptions>() ?? new AdminOptions();
var authEnabled = !string.IsNullOrEmpty(adminConfig.Password) || !string.IsNullOrEmpty(adminConfig.PasswordHash);
var requireHttps = builder.Configuration.GetValue<bool>("Inbix:RequireHttps");

builder.Services.AddSingleton<IAdminAuthenticator, AdminAuthenticator>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "inbix.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorization(options =>
{
    // When an admin password is configured, every endpoint requires login unless it opts out
    // with [AllowAnonymous]. With no password set, auth is disabled (open) for local dev.
    if (authEnabled)
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddCascadingAuthenticationState();

if (requireHttps)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

// Web: API + Blazor UI.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddOpenApi();

var app = builder.Build();

if (!authEnabled)
    app.Logger.LogWarning("Admin authentication is DISABLED: no Inbix:Admin:Password or PasswordHash is configured. The UI and API are open. Set a password before exposing Inbix.");

if (requireHttps)
{
    app.UseForwardedHeaders();
    if (!app.Environment.IsDevelopment())
        app.UseHsts();
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.MapInbixAuth();
app.MapInbixApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
