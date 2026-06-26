namespace Inbix.Core.Identities;

/// <summary>Which regional pools the generator may draw from. At least one should be enabled.</summary>
public sealed class GenerateOptions
{
    public bool IncludeUk { get; set; } = true;
    public bool IncludeUs { get; set; } = true;
}
