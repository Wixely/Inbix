using Inbix.Core.Validation;
using Xunit;

namespace Inbix.Tests;

public class AliasRulesTests
{
    [Theory]
    [InlineData("spotify")]
    [InlineData("git-hub")]
    [InlineData("my.alias")]
    [InlineData("under_score")]
    [InlineData("a1b2")]
    public void ValidLocalParts_Pass(string localPart)
        => Assert.Null(AliasRules.ValidateLocalPart(localPart));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("UPPER")]            // uppercase normalises, but raw contains caps -> still valid after normalize
    [InlineData(".leadingdot")]
    [InlineData("trailingdot.")]
    [InlineData("double..dot")]
    [InlineData("bad+plus")]
    public void InvalidOrNormalisedLocalParts(string localPart)
    {
        var result = AliasRules.ValidateLocalPart(localPart);
        if (localPart == "UPPER")
            Assert.Null(result); // normalised to lowercase, valid
        else
            Assert.NotNull(result);
    }

    [Theory]
    [InlineData("Spotify@MyDomain.com", "spotify", "mydomain.com")]
    [InlineData("a@b.co", "a", "b.co")]
    public void TrySplitAddress_Splits(string address, string expectedLocal, string expectedDomain)
    {
        Assert.True(AliasRules.TrySplitAddress(address, out var local, out var domain));
        Assert.Equal(expectedLocal, local);
        Assert.Equal(expectedDomain, domain);
    }

    [Theory]
    [InlineData("noat")]
    [InlineData("@nodomain")]
    [InlineData("nolocal@")]
    public void TrySplitAddress_RejectsMalformed(string address)
        => Assert.False(AliasRules.TrySplitAddress(address, out _, out _));
}
