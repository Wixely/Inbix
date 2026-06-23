using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

public interface IAuditRepository
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> ListAsync(int limit, int offset, CancellationToken ct = default);
}
