namespace Inbix.Core.Identities;

/// <summary>
/// The English-language countries the identity generator supports, keyed by a short country code
/// (what we store on an <see cref="Domain.Identity"/>). Names/streets are shared across all of them;
/// cities, phone format and postcode format are country-specific (see <c>RandomIdentityGenerator</c>).
/// </summary>
public static class Countries
{
    /// <summary>(code, display name) in display order.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All =
    [
        ("us", "United States"),
        ("uk", "United Kingdom"),
        ("ie", "Ireland"),
        ("ca", "Canada"),
        ("au", "Australia"),
        ("nz", "New Zealand"),
        ("za", "South Africa")
    ];

    /// <summary>Enabled by default until the user changes (and saves) the preference.</summary>
    public static readonly IReadOnlyList<string> DefaultCodes = ["us", "uk"];

    public static bool IsSupported(string? code) => code is not null && All.Any(c => c.Code == code);

    public static string Label(string? code)
    {
        foreach (var c in All)
            if (c.Code == code) return c.Name;
        return code?.ToUpperInvariant() ?? string.Empty;
    }
}
