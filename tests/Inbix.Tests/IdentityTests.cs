using Inbix.Core.Abstractions;
using Inbix.Core.Identities;
using Inbix.Core.Options;
using Inbix.Core.Validation;
using Inbix.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

public sealed class IdentityTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-identity-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var options = new InbixOptions
        {
            Domains = ["mydomain.com"],
            Database = { Provider = "sqlite", ConnectionString = $"Data Source={Path.Combine(_tempDir, "test.db")}" },
            Storage = { RawPath = Path.Combine(_tempDir, "raw") }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<InbixOptions>>(Options.Create(options));
        services.AddInbixData();
        _sp = services.BuildServiceProvider();

        await _sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
    }

    // ---- Generator (no DB) ----

    [Theory]
    [InlineData(true, false, "uk")]
    [InlineData(false, true, "us")]
    public void Generator_Honours_Region(bool uk, bool us, string expected)
    {
        var gen = new RandomIdentityGenerator();
        for (var i = 0; i < 25; i++)
            Assert.Equal(expected, gen.Generate(new GenerateOptions { IncludeUk = uk, IncludeUs = us }).Country);
    }

    [Fact]
    public void Generator_Produces_Valid_Adult_Identity()
    {
        var gen = new RandomIdentityGenerator();
        for (var i = 0; i < 25; i++)
        {
            var id = gen.Generate(new GenerateOptions());
            Assert.False(string.IsNullOrWhiteSpace(id.FirstName));
            Assert.False(string.IsNullOrWhiteSpace(id.LastName));
            Assert.False(string.IsNullOrWhiteSpace(id.Username));
            Assert.True(id.Password.Length >= 14, $"password too short: {id.Password.Length}");
            Assert.False(string.IsNullOrWhiteSpace(id.Street));
            Assert.False(string.IsNullOrWhiteSpace(id.City));
            Assert.False(string.IsNullOrWhiteSpace(id.Postcode));
            Assert.True(id.AgeYears >= 18, $"age was {id.AgeYears}");
            Assert.Contains(id.Country, new[] { "uk", "us" });
            Assert.Null(IdentityRules.Validate(id)); // generated identities are always valid
        }
    }

    [Fact]
    public void Generator_Empty_Region_Falls_Back_To_Both()
    {
        var id = new RandomIdentityGenerator().Generate(new GenerateOptions { IncludeUk = false, IncludeUs = false });
        Assert.Contains(id.Country, new[] { "uk", "us" });
    }

    // ---- Repository / service ----

    [Fact]
    public async Task Crud_Roundtrip_Including_DateOnly()
    {
        var repo = _sp.GetRequiredService<IIdentityRepository>();
        var service = _sp.GetRequiredService<IIdentityService>();

        var draft = new RandomIdentityGenerator().Generate(new GenerateOptions());
        draft.DateOfBirth = new DateOnly(1990, 6, 15);

        var created = await service.CreateAsync(draft);
        Assert.True(created.Id > 0);

        var fetched = await repo.GetByIdAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal(new DateOnly(1990, 6, 15), fetched!.DateOfBirth); // DateOnly handler round-trips
        Assert.Equal(draft.Username, fetched.Username);

        fetched.Username = "changed.name";
        await service.UpdateAsync(fetched);
        Assert.Equal("changed.name", (await repo.GetByIdAsync(created.Id))!.Username);

        await service.DeleteAsync(created.Id);
        Assert.Null(await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task Link_Sets_Email_And_GetByAlias_Works()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var repo = _sp.GetRequiredService<IIdentityRepository>();
        var service = _sp.GetRequiredService<IIdentityService>();

        var alias = await aliases.CreateAsync("spotify", "mydomain.com", null);
        var identity = await service.CreateAsync(new RandomIdentityGenerator().Generate(new GenerateOptions()));

        var linked = await service.LinkAsync(identity.Id, alias.Id);
        Assert.NotNull(linked);
        Assert.Equal(alias.Id, linked!.AliasId);
        Assert.Equal(alias.Address, linked.Email); // email auto-filled from the alias

        var byAlias = await repo.GetByAliasIdAsync(alias.Id);
        Assert.NotNull(byAlias);
        Assert.Equal(identity.Id, byAlias!.Id);

        var unlinked = await service.LinkAsync(identity.Id, null);
        Assert.Null(unlinked!.AliasId);
        Assert.Null(await repo.GetByAliasIdAsync(alias.Id));
    }

    [Fact]
    public async Task Deleting_Alias_Unlinks_Identity_But_Keeps_It()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var repo = _sp.GetRequiredService<IIdentityRepository>();
        var service = _sp.GetRequiredService<IIdentityService>();

        var alias = await aliases.CreateAsync("netflix", "mydomain.com", null);
        var identity = await service.CreateAsync(new RandomIdentityGenerator().Generate(new GenerateOptions()));
        await service.LinkAsync(identity.Id, alias.Id);

        await aliases.DeleteAsync(alias.Id); // FK ON DELETE SET NULL

        var survivor = await repo.GetByIdAsync(identity.Id);
        Assert.NotNull(survivor);
        Assert.Null(survivor!.AliasId); // unlinked, but the identity (and its details) survive
    }

    [Fact]
    public async Task One_Identity_Per_Alias_Is_Enforced()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var service = _sp.GetRequiredService<IIdentityService>();
        var gen = new RandomIdentityGenerator();

        var alias = await aliases.CreateAsync("github", "mydomain.com", null);

        var first = gen.Generate(new GenerateOptions());
        first.AliasId = alias.Id;
        await service.CreateAsync(first);

        var second = gen.Generate(new GenerateOptions());
        second.AliasId = alias.Id;
        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(second)); // UNIQUE(alias_id)
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
