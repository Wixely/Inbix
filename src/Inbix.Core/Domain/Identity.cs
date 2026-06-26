using Inbix.Core.Identities;

namespace Inbix.Core.Domain;

/// <summary>
/// A saved fake identity used for online registrations, optionally linked 1:1 to an alias so the
/// consistent details for that email (username, password, address, DOB, …) can be retrieved later.
/// Settable properties so Dapper can map snake_case columns; <see cref="DateOfBirth"/> uses the
/// registered DateOnly type handler.
/// </summary>
public sealed class Identity
{
    public long Id { get; set; }

    /// <summary>Region the identity was generated for: <c>"uk"</c> or <c>"us"</c>.</summary>
    public string Country { get; set; } = "uk";

    public string? Title { get; set; }
    public string? Gender { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }

    /// <summary>Email for registrations — auto-filled from the linked alias, but editable.</summary>
    public string? Email { get; set; }
    public string? Phone { get; set; }

    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string? StateCounty { get; set; }
    public string Postcode { get; set; } = string.Empty;

    public string? SecurityQuestion { get; set; }
    public string? SecurityAnswer { get; set; }

    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    public string CountryLabel => Countries.Label(Country);

    /// <summary>Age in whole years from <see cref="DateOfBirth"/> to today (UTC).</summary>
    public int AgeYears
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var age = today.Year - DateOfBirth.Year;
            if (DateOfBirth > today.AddYears(-age)) age--;
            return age < 0 ? 0 : age;
        }
    }

    /// <summary>Single-line address for display/copy.</summary>
    public string FullAddress => string.Join(", ", new[]
    {
        Street, City, StateCounty ?? string.Empty, Postcode, Country?.ToUpperInvariant() ?? string.Empty
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
