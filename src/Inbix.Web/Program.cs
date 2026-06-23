using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Smtp;
using Inbix.Web.Api;
using Inbix.Web.Components;
using Inbix.Worker;

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

// Web: API + Blazor UI.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseStaticFiles();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAntiforgery();

app.MapInbixApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
