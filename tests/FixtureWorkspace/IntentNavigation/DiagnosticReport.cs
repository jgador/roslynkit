namespace IntentNavigation;

/// <summary>
/// Represents one diagnostic report waiting to be validated before it is published.
/// </summary>
public sealed record DiagnosticReport(string Code, string Message);

/// <summary>
/// Records the deterministic outcome of validating a diagnostic report.
/// </summary>
public sealed record DiagnosticValidationResult(bool IsAccepted, string Route);
