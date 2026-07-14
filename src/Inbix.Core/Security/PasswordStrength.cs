namespace Inbix.Core.Security;

/// <summary>Lightweight password-strength heuristics used by diagnostics to nudge weak/default passwords.</summary>
public static class PasswordStrength
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "password", "passw0rd", "changeme", "change-me",
        "letmein", "inbix", "root", "12345678", "qwertyui", "secret",
    };

    /// <summary>Returns a short human reason when the password looks weak, or <c>null</c> when acceptable.</summary>
    public static string? Weakness(string? password)
    {
        var p = password ?? string.Empty;
        if (p.Length == 0) return "no password set";
        if (Common.Contains(p.Trim())) return "a common or default password";
        if (p.Length < 12) return "shorter than 12 characters";

        var classes =
            (p.Any(char.IsLower) ? 1 : 0) +
            (p.Any(char.IsUpper) ? 1 : 0) +
            (p.Any(char.IsDigit) ? 1 : 0) +
            (p.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (classes < 3) return "not a mix of upper/lower case, digits and symbols";

        return null;
    }
}
