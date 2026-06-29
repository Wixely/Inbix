using System.Text.RegularExpressions;

namespace Inbix.Core.Validation;

/// <summary>
/// Alias naming rules (per the plan): stored lowercase; letters, numbers, dots,
/// hyphens and underscores allowed; bounded length.
/// </summary>
public static partial class AliasRules
{
    public const int MinLength = 1;
    public const int MaxLength = 64;

    /// <summary>
    /// Local parts that collide with reserved folder names in the JSON file/folder store (<c>catchall</c>
    /// and <c>junk</c> are top-level folders alongside alias folders). Blocked in every provider so a
    /// database can be switched to JSON mode without conflicts.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedLocalParts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "catchall", "catch-all", "junk" };

    [GeneratedRegex(@"^[a-z0-9._-]+$")]
    private static partial Regex LocalPartPattern();

    /// <summary>Normalise a local part to canonical (lowercase, trimmed) form.</summary>
    public static string Normalize(string localPart) => localPart.Trim().ToLowerInvariant();

    /// <summary>Validate a local part. Returns null if valid, otherwise an error message.</summary>
    public static string? ValidateLocalPart(string localPart)
    {
        if (string.IsNullOrWhiteSpace(localPart))
            return "Alias local part is required.";

        var normalized = Normalize(localPart);
        if (normalized.Length < MinLength || normalized.Length > MaxLength)
            return $"Alias must be between {MinLength} and {MaxLength} characters.";

        if (!LocalPartPattern().IsMatch(normalized))
            return "Alias may only contain lowercase letters, numbers, dots, hyphens and underscores.";

        if (normalized.StartsWith('.') || normalized.EndsWith('.') || normalized.Contains(".."))
            return "Alias dots must be between other characters and not repeated.";

        if (ReservedLocalParts.Contains(normalized))
            return $"'{normalized}' is a reserved name and cannot be used as an alias.";

        return null;
    }

    /// <summary>Split an address into (localPart, domain), both lowercased. Returns false if malformed.</summary>
    public static bool TrySplitAddress(string address, out string localPart, out string domain)
    {
        localPart = string.Empty;
        domain = string.Empty;
        if (string.IsNullOrWhiteSpace(address)) return false;

        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1) return false;

        localPart = address[..at].Trim().ToLowerInvariant();
        domain = address[(at + 1)..].Trim().ToLowerInvariant();
        return localPart.Length > 0 && domain.Length > 0;
    }
}
