using System.Security.Cryptography;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Worker;

/// <summary>
/// Background worker that parses stored raw messages outside the SMTP transaction: it claims
/// unparsed messages, extracts headers/body/attachments with MimeKit, persists attachment bytes to
/// the raw store, and records the parsed metadata. Parsing can be re-run later from the raw source.
/// </summary>
public sealed class MimeParserWorker : BackgroundService
{
    private readonly IMessageRepository _messages;
    private readonly IRawMessageStore _rawStore;
    private readonly WorkerOptions _options;
    private readonly ILogger<MimeParserWorker> _logger;

    public MimeParserWorker(
        IMessageRepository messages, IRawMessageStore rawStore,
        IOptions<InbixOptions> options, ILogger<MimeParserWorker> logger)
    {
        _messages = messages;
        _rawStore = rawStore;
        _options = options.Value.Worker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        _logger.LogInformation("MIME parser worker started (every {Delay}s, batch {Batch})", delay.TotalSeconds, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
                // Only back off when there was nothing to do; otherwise keep draining the queue.
                if (processed == 0)
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MIME parser loop error; backing off");
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var batch = await _messages.ClaimUnparsedAsync(_options.BatchSize, ct).ConfigureAwait(false);
        foreach (var message in batch)
            await ProcessOneAsync(message, ct).ConfigureAwait(false);
        return batch.Count;
    }

    private async Task ProcessOneAsync(Message message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.RawStoragePath))
        {
            await _messages.MarkParseFailedAsync(message.Id, "No raw storage path recorded.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            ParsedEmail parsed;
            await using (var raw = await _rawStore.OpenReadAsync(message.RawStoragePath, ct).ConfigureAwait(false))
            {
                parsed = await MimeEmailParser.ParseAsync(raw, ct).ConfigureAwait(false);
            }

            var attachments = new List<Attachment>(parsed.Attachments.Count);
            foreach (var a in parsed.Attachments)
            {
                var storagePath = await _rawStore.SaveAttachmentAsync(a.FileName ?? "attachment", a.Content, ct).ConfigureAwait(false);
                attachments.Add(new Attachment
                {
                    MessageId = message.Id,
                    Filename = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.Content.LongLength,
                    StoragePath = storagePath,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(a.Content))
                });
            }

            var body = new MessageBody
            {
                MessageId = message.Id,
                TextBody = parsed.TextBody,
                HtmlBody = parsed.HtmlBody
            };

            await _messages.MarkParsedAsync(message.Id, parsed.Subject, parsed.Sender, parsed.MessageId, body, attachments, ct)
                .ConfigureAwait(false);

            _logger.LogInformation("Parsed message {MessageId} ({Attachments} attachment(s))", message.Id, attachments.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse message {MessageId}", message.Id);
            await _messages.MarkParseFailedAsync(message.Id, ex.Message, ct).ConfigureAwait(false);
        }
    }
}
