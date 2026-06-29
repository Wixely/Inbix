using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Smtp;
using Inbix.Web.Api;
using Inbix.Web.Auth;
using Inbix.Web.Components;
using Inbix.Web.Health;
using Inbix.Worker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

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
builder.Services.AddInbixData(builder.Configuration);   // SQLite / JSON store (provider-swappable)
builder.Services.AddInbixSmtp();   // SmtpServer receiver
builder.Services.AddInbixWorker(); // MIME parser background worker

// --- Authentication / authorization ---
var adminConfig = builder.Configuration.GetSection("Inbix:Admin").Get<AdminOptions>() ?? new AdminOptions();
var authEnabled = !string.IsNullOrEmpty(adminConfig.Password) || !string.IsNullOrEmpty(adminConfig.PasswordHash);
var requireHttps = builder.Configuration.GetValue<bool>("Inbix:RequireHttps");

builder.Services.AddSingleton<IAdminAuthenticator, AdminAuthenticator>();

// Persist DataProtection keys (which sign the auth cookie) under the data volume so logins survive
// container restarts/recreation. Defaults to the framework location when the path is not set.
var keysPath = builder.Configuration["Inbix:DataProtectionKeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    Directory.CreateDirectory(keysPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("Inbix");
}

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

// Throttle admin login to blunt online password guessing (per remote IP).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// Health checks: liveness (process) and readiness (database reachable).
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

// Diagnostics (status page): DNS lookups + public-IP probe.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DnsClient.ILookupClient>(_ => new DnsClient.LookupClient());
builder.Services.AddSingleton<Inbix.Web.Diagnostics.DiagnosticsService>();
builder.Services.AddHostedService<Inbix.Web.Diagnostics.DiagnosticsHostedService>();

// Serialize enums as strings in API responses (e.g. diagnostic status "Ok"/"Warning").
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Notifies the sidebar to refresh its inbox list when aliases change (per-circuit).
builder.Services.AddScoped<Inbix.Web.Services.AliasChangeNotifier>();
// Notifies the sidebar when a setting that affects it changes (e.g. Junk-inbox visibility).
builder.Services.AddScoped<Inbix.Web.Services.SettingsChangeNotifier>();

// Web: API + Blazor UI.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddOpenApi();

var app = builder.Build();

if (!authEnabled)
    app.Logger.LogWarning("Admin authentication is DISABLED: no Inbix:Admin:Password or PasswordHash is configured. The UI and API are open. Set a password before exposing Inbix.");

// Baseline security response headers for the admin UI/API.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

if (requireHttps)
{
    app.UseForwardedHeaders();
    if (!app.Environment.IsDevelopment())
        app.UseHsts();
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Serve static assets (incl. Blazor's _framework/*) from the published static-web-assets manifest
// rather than loose files, so framework JS resolves in the container too. AllowAnonymous keeps them
// reachable when the admin fallback auth policy is active.
app.MapStaticAssets().AllowAnonymous();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

// Liveness: process is up (runs no checks). Readiness: database reachable.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

app.MapInbixAuth();
app.MapInbixApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
