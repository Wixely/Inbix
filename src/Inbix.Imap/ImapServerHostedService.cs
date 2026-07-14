using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Imap;

/// <summary>
/// Hosts the read-only IMAP listener for the app lifetime. Returns immediately when IMAP is disabled.
/// Serves plaintext by default; wraps connections in TLS when a certificate is configured (implicit TLS).
/// </summary>
public sealed class ImapServerHostedService : BackgroundService
{
    private readonly ImapMailboxProvider _mailboxes;
    private readonly IRawMessageStore _rawStore;
    private readonly IInboxNotifier _notifier;
    private readonly ImapOptions _options;
    private readonly ILogger<ImapServerHostedService> _logger;

    public ImapServerHostedService(
        ImapMailboxProvider mailboxes, IRawMessageStore rawStore, IInboxNotifier notifier,
        IOptions<InbixOptions> options, ILogger<ImapServerHostedService> logger)
    {
        _mailboxes = mailboxes;
        _rawStore = rawStore;
        _notifier = notifier;
        _options = options.Value.Imap;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("IMAP server disabled (set Inbix:Imap:Enabled=true to enable).");
            return;
        }

        X509Certificate2? certificate = null;
        if (!string.IsNullOrWhiteSpace(_options.CertificatePath) && File.Exists(_options.CertificatePath))
            certificate = X509CertificateLoader.LoadPkcs12FromFile(_options.CertificatePath, _options.CertificatePassword);

        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        _logger.LogWarning(
            "Inbix IMAP (READ-ONLY) listening on port {Port} ({Tls}). Intended for trusted internal networks only — do not expose to the internet.",
            _options.Port, certificate is null ? "plaintext" : "TLS");

        var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentSessions));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleAsync(client, certificate, gate, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IMAP listener stopped unexpectedly");
        }
        finally
        {
            listener.Stop();
            certificate?.Dispose();
        }
    }

    private async Task HandleAsync(TcpClient client, X509Certificate2? certificate, SemaphoreSlim gate, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) { client.Dispose(); return; }
        try
        {
            using (client)
            {
                client.NoDelay = true;
                Stream stream = client.GetStream();
                SslStream? ssl = null;
                if (certificate is not null)
                {
                    ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false,
                        checkCertificateRevocation: false).ConfigureAwait(false);
                    stream = ssl;
                }

                try
                {
                    var session = new ImapSession(stream, _mailboxes, _rawStore, _options, _notifier, _logger);
                    await session.RunAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    ssl?.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "IMAP session ended with an error");
        }
        finally
        {
            gate.Release();
        }
    }
}
