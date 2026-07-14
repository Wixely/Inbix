using System.Security.Cryptography;
using System.Text;
using Inbix.Core.Options;
using Inbix.Core.Security;
using Microsoft.Extensions.Options;

namespace Inbix.Web.Auth;

public interface IAdminAuthenticator
{
    /// <summary>True when an admin password (plaintext or hash) is configured. When false, auth is disabled.</summary>
    bool Enabled { get; }

    string Username { get; }

    /// <summary>Constant-time validation of supplied credentials.</summary>
    bool Validate(string? username, string? password);
}

public sealed class AdminAuthenticator : IAdminAuthenticator
{
    private readonly AdminOptions _options;

    public AdminAuthenticator(IOptions<InbixOptions> options) => _options = options.Value.Admin;

    public bool Enabled =>
        !string.IsNullOrEmpty(_options.Password) || !string.IsNullOrEmpty(_options.PasswordHash);

    public string Username => string.IsNullOrWhiteSpace(_options.Username) ? "admin" : _options.Username;

    public bool Validate(string? username, string? password)
    {
        if (!Enabled) return false;

        var userOk = FixedTimeEquals(username ?? string.Empty, Username);

        bool passOk = !string.IsNullOrEmpty(_options.PasswordHash)
            ? PasswordHasher.Verify(password ?? string.Empty, _options.PasswordHash)
            : FixedTimeEquals(password ?? string.Empty, _options.Password);

        // Evaluate both regardless of the username result to avoid short-circuit timing leaks.
        return userOk && passOk;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
