namespace IntentNavigation;

/// <summary>
/// Defines the diagnostic-report validation boundary used before reports are published to telemetry.
/// </summary>
public interface IDiagnosticReportValidator
{
    /// <summary>
    /// Validates one diagnostic report and selects its publication route.
    /// </summary>
    DiagnosticValidationResult ValidateDiagnosticReport(DiagnosticReport report);
}
