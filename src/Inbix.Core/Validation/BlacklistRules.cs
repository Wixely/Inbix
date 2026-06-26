using System.Text.RegularExpressions;
using Inbix.Core.Domain;

namespace Inbix.Core.Validation;

/// <summary>Validation for blacklist rule patterns.</summary>
public static class BlacklistRules
{
    public const int MaxPatternLength = 512;

    /// <summary>Validate a rule pattern. Returns null if valid, otherwise an error message.</summary>
    public static string? ValidatePattern(RuleMatch matchType, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "Pattern is required.";

        if (pattern.Length > MaxPatternLength)
            return $"Pattern must be at most {MaxPatternLength} characters.";

        if (matchType == RuleMatch.Regex)
        {
            try { _ = new Regex(pattern, RegexOptions.IgnoreCase); }
            catch (ArgumentException ex) { return $"Invalid regular expression: {ex.Message}"; }
        }

        return null;
    }
}
