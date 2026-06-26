using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Services;

/// <summary>
/// Optional demo/dev data seeder (Inbix:SeedSampleData). On a database with no aliases yet, it
/// enables the catch-all, creates four mailboxes with a spread of messages, and adds a batch of
/// messages that only the catch-all accepts. Messages are fed through the real inbound sink so they
/// get raw storage and are parsed by the worker, exactly like genuine mail.
/// </summary>
public sealed class SampleDataSeeder : IHostedService
{
    private readonly IAliasRepository _aliases;
    private readonly IInboundMessageSink _sink;
    private readonly InbixOptions _options;
    private readonly ILogger<SampleDataSeeder> _logger;

    public SampleDataSeeder(IAliasRepository aliases, IInboundMessageSink sink, IOptions<InbixOptions> options, ILogger<SampleDataSeeder> logger)
    {
        _aliases = aliases;
        _sink = sink;
        _options = options.Value;
        _logger = logger;
    }

    private sealed record Mail(string From, string Subject, string Text, string? Html = null);

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_options.SeedSampleData)
            return;

        var domain = _options.Domains.Select(d => d.Trim()).FirstOrDefault(d => d.Length > 0);
        if (domain is null)
        {
            _logger.LogWarning("SeedSampleData is on but no domain is configured; skipping.");
            return;
        }

        var existing = await _aliases.ListAsync(ct);
        if (existing.Any(a => !a.IsCatchAll))
        {
            _logger.LogInformation("Sample data not seeded: aliases already exist.");
            return;
        }

        // Catch-all ON so the catch-all-only sample mail is visible.
        var catchAll = await _aliases.GetCatchAllAsync(ct);
        if (catchAll is { Enabled: false })
            await _aliases.UpdateAsync(catchAll.Id, enabled: true, notes: null, ct);

        var rng = new Random(20260624);
        var now = DateTimeOffset.UtcNow;
        var total = 0;

        foreach (var (local, pool) in MailboxPools())
        {
            await _aliases.CreateAsync(local, domain, "Sample mailbox", ct);
            var count = Math.Min(pool.Length, rng.Next(3, 9)); // 3-8
            foreach (var mail in Pick(pool, count, rng))
            {
                await FeedAsync($"{local}@{domain}", mail, RandomTime(now, rng), ct);
                total++;
            }
        }

        // 15 messages that match no alias -> only the catch-all accepts them, across 4 destinations.
        var catchAllTargets = new[] { "newsletter", "sales", "info", "careers" };
        var catchAllPool = CatchAllPool();
        for (var i = 0; i < 15; i++)
        {
            var recipient = $"{catchAllTargets[i % catchAllTargets.Length]}@{domain}";
            await FeedAsync(recipient, catchAllPool[i % catchAllPool.Length], RandomTime(now, rng), ct);
            total++;
        }

        _logger.LogInformation("Seeded {Total} sample messages across {Mailboxes} mailboxes + catch-all.", total, 4);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task FeedAsync(string recipient, Mail mail, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var raw = BuildRaw(mail.From, recipient, mail.Subject, mail.Text, mail.Html, receivedAt);
        var senderAddress = ExtractAddress(mail.From);
        var result = await _sink.SaveAsync(new InboundMessage
        {
            Recipient = recipient,
            Sender = senderAddress,
            RemoteIp = "198.51.100." + (Math.Abs(recipient.GetHashCode()) % 254 + 1),
            RawMime = raw,
            ReceivedAt = receivedAt
        }, ct);

        if (result != InboundSaveResult.Stored)
            _logger.LogWarning("Sample message to {Recipient} not stored ({Result}).", recipient, result);
    }

    private static DateTimeOffset RandomTime(DateTimeOffset now, Random rng) => now.AddMinutes(-rng.Next(2, 7800));

    private static IEnumerable<Mail> Pick(Mail[] pool, int count, Random rng) =>
        pool.OrderBy(_ => rng.Next()).Take(count);

    private static string ExtractAddress(string from)
    {
        var lt = from.IndexOf('<');
        var gt = from.IndexOf('>');
        return lt >= 0 && gt > lt ? from[(lt + 1)..gt] : from;
    }

    private static byte[] BuildRaw(string from, string to, string subject, string text, string? html, DateTimeOffset date)
    {
        var sb = new StringBuilder();
        sb.Append("From: ").Append(from).Append("\r\n");
        sb.Append("To: ").Append(to).Append("\r\n");
        sb.Append("Subject: ").Append(subject).Append("\r\n");
        sb.Append("Date: ").Append(date.ToUniversalTime().ToString("r")).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");

        if (html is null)
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
            sb.Append(text).Append("\r\n");
        }
        else
        {
            const string b = "INBIXALT";
            sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(b).Append("\"\r\n\r\n");
            sb.Append("--").Append(b).Append("\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n").Append(text).Append("\r\n");
            sb.Append("--").Append(b).Append("\r\nContent-Type: text/html; charset=utf-8\r\n\r\n").Append(html).Append("\r\n");
            sb.Append("--").Append(b).Append("--\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static IEnumerable<(string Local, Mail[] Pool)> MailboxPools()
    {
        yield return ("spotify", new[]
        {
            new Mail("Spotify <noreply@spotify.com>", "Your Discover Weekly is ready", "Fresh tracks picked for you this week. Listen now: https://open.spotify.com/playlist/discover-weekly",
                "<html><body><h1>Discover Weekly</h1><img src=\"http://t.spotify.com/px.gif\"><p>Fresh tracks picked for you this week.</p></body></html>"),
            new Mail("Spotify <noreply@spotify.com>", "Your receipt from Spotify", "Thanks for your payment of 9.99 for Spotify Premium. View your receipt at https://www.spotify.com/account/order-history/"),
            new Mail("Spotify <noreply@spotify.com>", "New release from an artist you follow", "The album you have been waiting for is out now: https://open.spotify.com/album/new-release"),
            new Mail("Spotify <noreply@spotify.com>", "Your offline playlist is ready", "We saved your playlist so you can listen anywhere."),
            new Mail("Spotify <noreply@spotify.com>", "Your password was changed", "Your Spotify password was changed successfully. If this was not you, secure your account at https://accounts.spotify.com/password-reset"),
            new Mail("Spotify <noreply@spotify.com>", "3 months of Premium on us", "Come back to ad-free listening with 3 months free: https://www.spotify.com/premium/"),
            new Mail("Spotify <noreply@spotify.com>", "Wrapped is almost here", "Your year in music is being prepared."),
            new Mail("Spotify <noreply@spotify.com>", "Concerts near you", "Artists you follow are playing live soon. Tickets: https://www.spotify.com/concerts"),
        });

        yield return ("github", new[]
        {
            new Mail("GitHub <security@github.com>", "New sign-in to your account", "We noticed a new sign-in from a new device. Review it at https://github.com/settings/security-log"),
            new Mail("GitHub <notifications@github.com>", "PR #42 was merged", "Your pull request was merged into main: https://github.com/octocat/inbix/pull/42",
                "<html><body><h2>Merged</h2><p>Your pull request <b>#42</b> was merged into <code>main</code>.</p></body></html>"),
            new Mail("GitHub <security@github.com>", "Security alert: vulnerable dependency", "A dependency in one of your repositories has a known vulnerability. Details: https://github.com/octocat/inbix/security/dependabot"),
            new Mail("GitHub <notifications@github.com>", "You have 5 new notifications", "Catch up on activity at https://github.com/notifications"),
            new Mail("GitHub <notifications@github.com>", "Your weekly digest", "Here is what happened across your repositories this week."),
            new Mail("GitHub <actions@github.com>", "Deploy succeeded", "Your GitHub Actions workflow completed successfully. View the run: https://github.com/octocat/inbix/actions/runs/12345"),
            new Mail("GitHub <notifications@github.com>", "octocat mentioned you", "You were mentioned in an issue comment: https://github.com/octocat/inbix/issues/7"),
        });

        yield return ("amazon", new[]
        {
            new Mail("Amazon <ship-confirm@amazon.com>", "Your order has shipped", "Order 112-4458 is on its way. Track it at https://www.amazon.com/gp/your-account/order-details?orderId=112-4458"),
            new Mail("Amazon <auto-confirm@amazon.com>", "Your Amazon order #114-220", "Thanks for your order. We will let you know when it ships."),
            new Mail("Amazon <ship-confirm@amazon.com>", "Delivered: your package", "Your package was left at the front door. A problem? Visit https://www.amazon.com/gp/help"),
            new Mail("Amazon <deals@amazon.com>", "Deal of the day", "Today only: up to 40 percent off electronics. Shop now: https://www.amazon.com/deals",
                "<html><body><h1>Deal of the Day</h1><img src=\"http://ads.amazon.com/banner.png\"><p>Up to 40% off electronics.</p></body></html>"),
            new Mail("Amazon <auto-confirm@amazon.com>", "Your subscription renews soon", "Prime renews on the 28th. Manage it at https://www.amazon.com/prime"),
            new Mail("Amazon <auto-confirm@amazon.com>", "Refund issued", "We have issued a refund to your original payment method."),
        });

        yield return ("netflix", new[]
        {
            new Mail("Netflix <info@netflix.com>", "New on Netflix this week", "Fresh titles just landed in your region. Browse them: https://www.netflix.com/browse"),
            new Mail("Netflix <info@netflix.com>", "Your bill", "Your monthly Netflix charge of 15.49 has been processed. See https://www.netflix.com/account"),
            new Mail("Netflix <info@netflix.com>", "Finish watching", "Pick up where you left off: https://www.netflix.com/continue"),
            new Mail("Netflix <info@netflix.com>", "New device using your account", "A new device started watching. Not you? https://www.netflix.com/account/security"),
            new Mail("Netflix <info@netflix.com>", "We added a title to your list", "It is ready to watch now."),
            new Mail("Netflix <info@netflix.com>", "Password reset requested", "Use this link to reset your password: https://www.netflix.com/password"),
        });
    }

    private static Mail[] CatchAllPool() => new[]
    {
        new Mail("marketing@acme.io", "Partnership opportunity", "We would love to explore working together. More at https://acme.io/partners"),
        new Mail("deals@shopnow.com", "Flash sale ends tonight", "Up to 70 percent off everything in store: https://shopnow.com/flash-sale"),
        new Mail("no-reply@survey.co", "We value your feedback", "Take our 2-minute survey and help us improve: https://survey.co/r/inbix"),
        new Mail("team@startup.dev", "Try our new API", "Build faster with our developer platform. Read the docs: https://startup.dev/docs"),
        new Mail("hr@recruit.com", "A job opportunity for you", "We think you would be a great fit. Apply at https://recruit.com/jobs/8842"),
        new Mail("billing@hosting.net", "Invoice attached", "Your monthly hosting invoice is available at https://hosting.net/billing/invoices"),
        new Mail("events@confs.org", "You are invited to DevConf", "Join 5,000 engineers this autumn. Register: https://confs.org/devconf/register"),
        new Mail("press@news.com", "This week's headlines", "Top stories in technology, curated for you: https://news.com/tech"),
        new Mail("support@vendor.io", "Ticket #882 updated", "We have responded to your support request: https://vendor.io/tickets/882"),
        new Mail("promo@travel.com", "Cheap flights this weekend", "Deals departing from your city: https://travel.com/deals"),
        new Mail("noreply@bank-alerts.com", "Your statement is ready", "Your monthly account statement is available to view."),
        new Mail("hello@designhub.co", "New templates dropped", "Fresh designs for your next project: https://designhub.co/templates"),
        new Mail("updates@social.app", "You have 12 new followers", "See who started following you this week: https://social.app/followers"),
        new Mail("winner@prize-draw.net", "You may have already won", "Claim your prize before it expires: http://prize-draw.net/claim?id=99213"),
        new Mail("newsletter@weekly.dev", "The Weekly Dev #210", "Curated links and tools for developers: https://weekly.dev/issues/210"),
    };
}
