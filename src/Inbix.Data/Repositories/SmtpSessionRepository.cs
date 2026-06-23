using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Repositories;

public sealed class SmtpSessionRepository : ISmtpSessionRepository
{
    private readonly IDbConnectionFactory _factory;

    public SmtpSessionRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(SmtpSession s, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleAsync<long>(
            """
            INSERT INTO smtp_sessions (remote_ip, helo, mail_from, started_at)
            VALUES (@RemoteIp, @Helo, @MailFrom, @StartedAt)
            RETURNING id;
            """, s).ConfigureAwait(false);
    }

    public async Task CompleteAsync(long id, string result, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(
            "UPDATE smtp_sessions SET result = @result, ended_at = @endedAt WHERE id = @id;",
            new { id, result, endedAt }).ConfigureAwait(false);
    }
}
