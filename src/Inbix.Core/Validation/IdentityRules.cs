using Inbix.Core.Domain;
using Inbix.Core.Identities;

namespace Inbix.Core.Validation;

/// <summary>Validation for saved identities. Mirrors <see cref="AliasRules"/>: returns null if valid.</summary>
public static class IdentityRules
{
    public const int MaxNameLength = 80;
    public const int MaxFieldLength = 256;

    /// <summary>Validate an identity before persisting. Returns null if valid, else an error message.</summary>
    public static string? Validate(Identity i)
    {
        if (string.IsNullOrWhiteSpace(i.FirstName)) return "First name is required.";
        if (string.IsNullOrWhiteSpace(i.LastName)) return "Last name is required.";
        if (string.IsNullOrWhiteSpace(i.Username)) return "Username is required.";
        if (string.IsNullOrWhiteSpace(i.Password)) return "Password is required.";

        if (i.FirstName.Length > MaxNameLength || i.LastName.Length > MaxNameLength)
            return $"Names must be at most {MaxNameLength} characters.";
        if (i.Username.Length > MaxFieldLength || i.Password.Length > MaxFieldLength)
            return $"Username and password must be at most {MaxFieldLength} characters.";

        if (!Countries.IsSupported(i.Country?.ToLowerInvariant())) return "Unknown region.";

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (i.DateOfBirth == default) return "Date of birth is required.";
        if (i.DateOfBirth > today) return "Date of birth cannot be in the future.";
        if (i.DateOfBirth < today.AddYears(-120)) return "Date of birth is implausible.";

        return null;
    }
}
