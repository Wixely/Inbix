namespace Inbix.Web.Diagnostics;

public enum DiagnosticStatus { Ok, Warning, Error, Info }

/// <summary>One diagnostic check outcome shown on the status page.</summary>
public sealed record DiagnosticResult(
    string Category,
    string Name,
    DiagnosticStatus Status,
    string Message,
    string? Detail = null);
