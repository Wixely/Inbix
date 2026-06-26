using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Inbix.Data.Services;

/// <summary>Identity CRUD + alias linking, with audit logging. Wraps <see cref="IIdentityRepository"/>.</summary>
public sealed class IdentityService : IIdentityService
{
    private readonly IIdentityRepository _identities;
    private readonly IAliasRepository _aliases;
    private readonly IAuditRepository _audit;
    private readonly ILogger<IdentityService> _logger;

    public IdentityService(
        IIdentityRepository identities, IAliasRepository aliases,
        IAuditRepository audit, ILogger<IdentityService> logger)
    {
        _identities = identities;
        _aliases = aliases;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Identity> CreateAsync(Identity identity, CancellationToken ct = default)
    {
        var created = await _identities.CreateAsync(identity, ct).ConfigureAwait(false);
        await Audit("identity.create", created.Id, ct).ConfigureAwait(false);
        return created;
    }

    public async Task<Identity?> UpdateAsync(Identity identity, CancellationToken ct = default)
    {
        var updated = await _identities.UpdateAsync(identity, ct).ConfigureAwait(false);
        if (updated is not null) await Audit("identity.update", updated.Id, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _identities.DeleteAsync(id, ct).ConfigureAwait(false);
        await Audit("identity.delete", id, ct).ConfigureAwait(false);
    }

    public async Task<Identity?> LinkAliasAsync(long aliasId, long? identityId, CancellationToken ct = default)
    {
        await _aliases.SetIdentityAsync(aliasId, identityId, ct).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry
        {
            Action = identityId is null ? "identity.unlink" : "identity.link",
            TargetType = "alias", TargetId = aliasId.ToString(),
            Actor = "ui", CreatedAt = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);

        return identityId is long id ? await _identities.GetByIdAsync(id, ct).ConfigureAwait(false) : null;
    }

    private Task Audit(string action, long id, CancellationToken ct) =>
        _audit.WriteAsync(new AuditEntry
        {
            Action = action, TargetType = "identity", TargetId = id.ToString(),
            Actor = "ui", CreatedAt = DateTimeOffset.UtcNow
        }, ct);
}
