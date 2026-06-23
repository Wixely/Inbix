using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

public interface ISmtpSessionRepository
{
    Task<long> CreateAsync(SmtpSession session, CancellationToken ct = default);

    Task CompleteAsync(long id, string result, DateTimeOffset endedAt, CancellationToken ct = default);
}
