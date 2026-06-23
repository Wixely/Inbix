using System.Security.Cryptography.X509Certificates;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;

namespace Inbix.Smtp;

/// <summary>
/// Hosts the SmtpServer listener for the lifetime of the application. The server resolves its
/// message store and mailbox filter from the application's service provider.
/// </summary>
public sealed class SmtpServerHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SmtpConnectionGovernor _governor;
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpServerHostedService> _logger;

    public SmtpServerHostedService(
        IServiceProvider serviceProvider, SmtpConnectionGovernor governor,
        IOptions<InbixOptions> options, ILogger<SmtpServerHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _governor = governor;
        _options = options.Value.Smtp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxSize = (int)Math.Min(_options.MaxMessageSizeBytes, int.MaxValue);

        X509Certificate2? certificate = null;
        if (!string.IsNullOrWhiteSpace(_options.CertificatePath) && File.Exists(_options.CertificatePath))
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(_options.CertificatePath, _options.CertificatePassword);
            _logger.LogInformation("STARTTLS enabled using certificate {Path}", _options.CertificatePath);
        }

        var optionsBuilder = new SmtpServerOptionsBuilder()
            .ServerName(_options.ServerName)
            .MaxMessageSize(maxSize)
            .Endpoint(endpoint =>
            {
                endpoint.Port(_options.Port, isSecure: false);
                if (certificate is not null)
                {
                    endpoint.Certificate(certificate);
                    endpoint.AllowUnsecureAuthentication(true);
                }
            });

        var server = new SmtpServer.SmtpServer(optionsBuilder.Build(), _serviceProvider);

        // Track active sessions for the connection governor (concurrency cap + per-IP rate limit).
        server.SessionCreated += (_, _) => _governor.SessionStarted();
        server.SessionCompleted += (_, _) => _governor.SessionEnded();
        server.SessionFaulted += (_, _) => _governor.SessionEnded();
        server.SessionCancelled += (_, _) => _governor.SessionEnded();

        _logger.LogInformation("Inbix SMTP receiver listening on port {Port} (max {MaxSize} bytes, {MaxSessions} concurrent)",
            _options.Port, maxSize, _options.MaxConcurrentSessions);

        try
        {
            await server.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "SMTP receiver stopped unexpectedly");
            throw;
        }
    }
}
