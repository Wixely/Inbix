using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Json.Repositories;

/// <summary>File-backed <see cref="ISmtpSessionRepository"/>. Sessions are write-only audit data (never
/// queried back), so each is a small file under <c>sessions/</c> rather than part of the in-memory index.</summary>
public sealed class JsonSmtpSessionRepository : ISmtpSessionRepository
{
    private readonly JsonDataStore _store;

    public JsonSmtpSessionRepository(JsonDataStore store) => _store = store;

    public Task<long> CreateAsync(SmtpSession session, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            session.Id = _store.NextSessionId();
            await _store.Io.WriteAsync(_store.SessionPath(session.Id), session, c).ConfigureAwait(false);
            return session.Id;
        }, ct);

    public Task CompleteAsync(long id, string result, DateTimeOffset endedAt, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            var path = _store.SessionPath(id);
            var session = await _store.Io.ReadAsync<SmtpSession>(path, c).ConfigureAwait(false);
            if (session is null) return;
            session.Result = result;
            session.EndedAt = endedAt;
            await _store.Io.WriteAsync(path, session, c).ConfigureAwait(false);
        }, ct);
}
