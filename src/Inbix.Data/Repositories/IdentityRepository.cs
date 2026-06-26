using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Repositories;

public sealed class IdentityRepository : IIdentityRepository
{
    private const string Columns =
        "id, alias_id, country, title, gender, first_name, last_name, username, password, " +
        "date_of_birth, email, phone, street, city, state_county, postcode, " +
        "security_question, security_answer, notes, created_at";

    private readonly IDbConnectionFactory _factory;

    public IdentityRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Identity>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<Identity>(
            $"SELECT {Columns} FROM identities ORDER BY created_at DESC, id DESC;").ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<Identity?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Identity>(
            $"SELECT {Columns} FROM identities WHERE id = @id;", new { id }).ConfigureAwait(false);
    }

    public async Task<Identity?> GetByAliasIdAsync(long aliasId, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Identity>(
            $"SELECT {Columns} FROM identities WHERE alias_id = @aliasId;", new { aliasId }).ConfigureAwait(false);
    }

    public async Task<Identity> CreateAsync(Identity identity, CancellationToken ct = default)
    {
        identity.CreatedAt = DateTimeOffset.UtcNow;
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        // The Identity object's PascalCase properties bind to the @Params; DateOnly/DateTimeOffset go
        // through their registered type handlers.
        return await c.QuerySingleAsync<Identity>(
            $"""
             INSERT INTO identities
                 (alias_id, country, title, gender, first_name, last_name, username, password,
                  date_of_birth, email, phone, street, city, state_county, postcode,
                  security_question, security_answer, notes, created_at)
             VALUES
                 (@AliasId, @Country, @Title, @Gender, @FirstName, @LastName, @Username, @Password,
                  @DateOfBirth, @Email, @Phone, @Street, @City, @StateCounty, @Postcode,
                  @SecurityQuestion, @SecurityAnswer, @Notes, @CreatedAt)
             RETURNING {Columns};
             """, identity).ConfigureAwait(false);
    }

    public async Task<Identity?> UpdateAsync(Identity identity, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Identity>(
            $"""
             UPDATE identities SET
                 alias_id = @AliasId, country = @Country, title = @Title, gender = @Gender,
                 first_name = @FirstName, last_name = @LastName, username = @Username, password = @Password,
                 date_of_birth = @DateOfBirth, email = @Email, phone = @Phone,
                 street = @Street, city = @City, state_county = @StateCounty, postcode = @Postcode,
                 security_question = @SecurityQuestion, security_answer = @SecurityAnswer, notes = @Notes
             WHERE id = @Id
             RETURNING {Columns};
             """, identity).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync("DELETE FROM identities WHERE id = @id;", new { id }).ConfigureAwait(false);
    }
}
