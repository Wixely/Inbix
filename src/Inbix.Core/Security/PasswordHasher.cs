using System.Security.Cryptography;

namespace Inbix.Core.Security;

/// <summary>
/// PBKDF2 (SHA-256) password hashing. Hash format: "pbkdf2-sha256$iterations$saltB64$keyB64".
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    // OWASP (2023) recommends >= 600k iterations for PBKDF2-HMAC-SHA256. The iteration count is
    // stored in each hash, so existing hashes keep verifying after this value changes.
    private const int Iterations = 600_000;
    private const int SaltLength = 16;
    private const int KeyLength = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != Prefix) return false;

            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var key = Convert.FromBase64String(parts[3]);

            var test = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, key.Length);
            return CryptographicOperations.FixedTimeEquals(test, key);
        }
        catch
        {
            return false;
        }
    }
}
