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
    [InlineData("uk")]
    [InlineData("us")]
    [InlineData("ie")]
    [InlineData("au")]
    [InlineData("za")]
    public void Generator_Honours_Single_Region(string code)
    {
        var gen = new RandomIdentityGenerator();
        for (var i = 0; i < 25; i++)
            Assert.Equal(code, gen.Generate(new GenerateOptions { Countries = [code] }).Country);
    }

    [Fact]
    public void Generator_Picks_Only_From_Enabled_Regions()
    {
        var gen = new RandomIdentityGenerator();
        var allowed = new[] { "ca", "nz" };
        for (var i = 0; i < 40; i++)
            Assert.Contains(gen.Generate(new GenerateOptions { Countries = allowed }).Country, allowed);
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
    public void Generator_Empty_Region_Falls_Back_To_Defaults()
    {
        var id = new RandomIdentityGenerator().Generate(new GenerateOptions { Countries = [] });
        Assert.Contains(id.Country, Countries.DefaultCodes);
    }

    [Fact]
    public void Usernames_Are_Dictionary_Word_Based_And_Varied()
    {
        var gen = new RandomIdentityGenerator();
        var seen = new HashSet<string>();
        for (var i = 0; i < 60; i++)
        {
            var u = gen.NewUsername();
            Assert.False(string.IsNullOrWhiteSpace(u));
            Assert.True(u.Length is >= 3 and <= 48, $"unexpected length: {u}");
            Assert.Matches("^[A-Za-z][A-Za-z0-9_.]*$", u); // a word, then letters/digits/_/. only
            seen.Add(u);
        }
        Assert.True(seen.Count >= 30, $"expected variety, got {seen.Count} unique"); // combinatorial mix
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
    public async Task Identity_Can_Link_To_Multiple_Aliases()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var service = _sp.GetRequiredService<IIdentityService>();

        var a1 = await aliases.CreateAsync("spotify", "mydomain.com", null);
        var a2 = await aliases.CreateAsync("netflix", "mydomain.com", null);
        var identity = await service.CreateAsync(new RandomIdentityGenerator().Generate(new GenerateOptions()));

        await service.LinkAliasAsync(a1.Id, identity.Id);
        await service.LinkAliasAsync(a2.Id, identity.Id);

        var linked = await aliases.ListByIdentityAsync(identity.Id);
        Assert.Equal(2, linked.Count);
        Assert.Contains(linked, a => a.Id == a1.Id);
        Assert.Contains(linked, a => a.Id == a2.Id);
        Assert.Equal(identity.Id, (await aliases.GetByIdAsync(a1.Id))!.IdentityId);
    }

    [Fact]
    public async Task Alias_Holds_At_Most_One_Identity_And_Can_Be_Unlinked()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var service = _sp.GetRequiredService<IIdentityService>();
        var gen = new RandomIdentityGenerator();

        var alias = await aliases.CreateAsync("github", "mydomain.com", null);
        var first = await service.CreateAsync(gen.Generate(new GenerateOptions()));
        var second = await service.CreateAsync(gen.Generate(new GenerateOptions()));

        await service.LinkAliasAsync(alias.Id, first.Id);
        Assert.Equal(first.Id, (await aliases.GetByIdAsync(alias.Id))!.IdentityId);

        // Re-linking replaces — an alias points at one identity.
        await service.LinkAliasAsync(alias.Id, second.Id);
        Assert.Equal(second.Id, (await aliases.GetByIdAsync(alias.Id))!.IdentityId);

        await service.LinkAliasAsync(alias.Id, null);
        Assert.Null((await aliases.GetByIdAsync(alias.Id))!.IdentityId);
    }

    [Fact]
    public async Task Deleting_Identity_Unlinks_Its_Aliases_But_Keeps_Them()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var service = _sp.GetRequiredService<IIdentityService>();

        var a1 = await aliases.CreateAsync("spotify", "mydomain.com", null);
        var a2 = await aliases.CreateAsync("netflix", "mydomain.com", null);
        var identity = await service.CreateAsync(new RandomIdentityGenerator().Generate(new GenerateOptions()));
        await service.LinkAliasAsync(a1.Id, identity.Id);
        await service.LinkAliasAsync(a2.Id, identity.Id);

        await service.DeleteAsync(identity.Id); // FK ON DELETE SET NULL

        Assert.NotNull(await aliases.GetByIdAsync(a1.Id));             // aliases survive
        Assert.Null((await aliases.GetByIdAsync(a1.Id))!.IdentityId);  // but are unlinked
        Assert.Null((await aliases.GetByIdAsync(a2.Id))!.IdentityId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
