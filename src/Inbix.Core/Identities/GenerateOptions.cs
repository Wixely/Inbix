namespace Inbix.Core.Identities;

/// <summary>
/// Which country pools the generator may draw from (by code, e.g. "us", "uk", "au"). Null or empty
/// falls back to <see cref="Countries.DefaultCodes"/>.
/// </summary>
public sealed class GenerateOptions
{
    public IReadOnlyList<string>? Countries { get; set; }
}
