using Inbix.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Inbix.Data.Services;

/// <summary>
/// Coordinates alias create/delete with catch-all message migration so mail follows the address:
/// creating an alias pulls in catch-all mail already addressed to it; deleting an alias returns its
/// mail to the catch-all rather than orphaning it.
/// </summary>
public sealed class AliasService : IAliasService
{
    private readonly IAliasRepository _aliases;
    private readonly IMessageRepository _messages;
    private readonly ILogger<AliasService> _logger;

    public AliasService(IAliasRepository aliases, IMessageRepository messages, ILogger<AliasService> logger)
    {
        _aliases = aliases;
        _messages = messages;
        _logger = logger;
    }

    public async Task<AliasCreated> CreateAsync(string localPart, string domain, string? notes, CancellationToken ct = default)
    {
        var alias = await _aliases.CreateAsync(localPart, domain, notes, ct).ConfigureAwait(false);

        var migrated = 0;
        var catchAll = await _aliases.GetCatchAllAsync(ct).ConfigureAwait(false);
        if (catchAll is not null)
        {
            migrated = await _messages.ReassignByRecipientAsync(catchAll.Id, alias.Id, alias.Address, ct).ConfigureAwait(false);
            if (migrated > 0)
                _logger.LogInformation("Claimed {Count} catch-all message(s) for new alias {Address}", migrated, alias.Address);
        }

        return new AliasCreated(alias, migrated);
    }

    public async Task<int> DeleteAsync(long aliasId, CancellationToken ct = default)
    {
        var alias = await _aliases.GetByIdAsync(aliasId, ct).ConfigureAwait(false);
        if (alias is null)
            return 0;
        if (alias.IsCatchAll)
            throw new InvalidOperationException("The catch-all cannot be deleted.");

        var migrated = 0;
        var catchAll = await _aliases.GetCatchAllAsync(ct).ConfigureAwait(false);
        if (catchAll is not null)
            migrated = await _messages.ReassignAllAsync(aliasId, catchAll.Id, ct).ConfigureAwait(false);

        await _aliases.DeleteAsync(aliasId, ct).ConfigureAwait(false);
        _logger.LogInformation("Deleted alias {Address}; moved {Count} message(s) to the catch-all", alias.Address, migrated);
        return migrated;
    }
}
