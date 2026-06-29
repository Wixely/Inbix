using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Json.Repositories;

/// <summary>File-backed <see cref="IIdentityRepository"/>; all identities live in <c>identities.json</c>.</summary>
public sealed class JsonIdentityRepository : IIdentityRepository
{
    private readonly JsonDataStore _store;

    public JsonIdentityRepository(JsonDataStore store) => _store = store;

    public Task<IReadOnlyList<Identity>> ListAsync(CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<Identity>)_store.Identities
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Select(Clone).ToList(), ct);

    public Task<Identity?> GetByIdAsync(long id, CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Identities.FirstOrDefault(i => i.Id == id) is { } i ? Clone(i) : null, ct);

    public Task<Identity> CreateAsync(Identity identity, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            var created = Clone(identity);
            created.Id = _store.NextIdentityId();
            created.CreatedAt = DateTimeOffset.UtcNow;
            _store.Identities.Add(created);
            await _store.SaveIdentitiesAsync(c).ConfigureAwait(false);
            return Clone(created);
        }, ct);

    public Task<Identity?> UpdateAsync(Identity identity, CancellationToken ct = default) =>
        _store.WriteAsync<Identity?>(async c =>
        {
            var existing = _store.Identities.FirstOrDefault(i => i.Id == identity.Id);
            if (existing is null) return null;
            CopyEditable(identity, existing);
            await _store.SaveIdentitiesAsync(c).ConfigureAwait(false);
            return Clone(existing);
        }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            if (_store.Identities.RemoveAll(i => i.Id == id) == 0) return;
            await _store.SaveIdentitiesAsync(c).ConfigureAwait(false);

            // Mirror the SQL "ON DELETE SET NULL": unlink any aliases that referenced this identity.
            foreach (var a in _store.Aliases.Values.Where(a => a.Alias.IdentityId == id))
            {
                a.Alias.IdentityId = null;
                await _store.PersistAliasAsync(a, c).ConfigureAwait(false);
            }
        }, ct);

    private static void CopyEditable(Identity from, Identity to)
    {
        to.Country = from.Country;
        to.Title = from.Title;
        to.Gender = from.Gender;
        to.FirstName = from.FirstName;
        to.LastName = from.LastName;
        to.Username = from.Username;
        to.Password = from.Password;
        to.DateOfBirth = from.DateOfBirth;
        to.Email = from.Email;
        to.Phone = from.Phone;
        to.Street = from.Street;
        to.City = from.City;
        to.StateCounty = from.StateCounty;
        to.Postcode = from.Postcode;
        to.SecurityQuestion = from.SecurityQuestion;
        to.SecurityAnswer = from.SecurityAnswer;
        to.Notes = from.Notes;
    }

    private static Identity Clone(Identity i)
    {
        var clone = new Identity { Id = i.Id, CreatedAt = i.CreatedAt };
        CopyEditable(i, clone);
        return clone;
    }
}
