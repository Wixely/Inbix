using Inbix.Core.Domain;
using Inbix.Core.Identities;

namespace Inbix.Core.Abstractions;

/// <summary>Produces a random, unsaved <see cref="Identity"/> from offline data pools.</summary>
public interface IIdentityGenerator
{
    /// <summary>Generate a fresh identity. Honours the region flags in <paramref name="options"/>.</summary>
    Identity Generate(GenerateOptions options);

    /// <summary>A fresh strong password (for per-field "regenerate" in the editor).</summary>
    string NewPassword();

    /// <summary>A fresh username derived from a name (for per-field "regenerate").</summary>
    string NewUsername(string firstName, string lastName);
}
